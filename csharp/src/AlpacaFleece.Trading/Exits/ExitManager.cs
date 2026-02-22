namespace AlpacaFleece.Trading.Exits;

/// <summary>
/// Exit manager: checks positions every 30s for stop loss, trailing stop, profit target.
/// Uses a 3-rule priority system with ATR-based dynamic levels (mutual exclusion with fixed-%):
///
///   Rule 1: ATR stop loss     — entry - (ATR × AtrStopLossMultiplier)
///   Rule 2: ATR profit target — entry + (ATR × AtrProfitTargetMultiplier)
///   Rule 4: Trailing stop     — TrailingStopPrice (always active)
///
/// ATR mutual exclusion (atr_computed): when valid ATR levels exist, fixed-percentage rules
/// (3 and 5) are skipped entirely. The early ATR-validity guard ensures we never reach the
/// fixed-% checks when ATR is valid, so fixed-% rules are effectively disabled in production
/// (ATR is required to open a position).
///
/// Publishes ExitSignalEvent to unbounded event bus (never dropped).
/// PendingExit flag is set AFTER successful publish to avoid phantom locks on bus failures.
/// </summary>
public class ExitManager(
    IPositionTracker positionTracker,
    IBrokerService brokerService,
    IMarketDataClient marketDataClient,
    IEventBus eventBus,
    IStateRepository stateRepository,
    ILogger<ExitManager> logger,
    IOptions<TradingOptions> options)
{
    // Protected no-arg constructor for NSubstitute proxy creation
    protected ExitManager() : this(null!, null!, null!, null!, null!, null!, Options.Create(new TradingOptions())) { }

    private readonly ExitOptions _options = options.Value.Exit;
    private readonly HashSet<string> _cryptoSymbols =
        new(options.Value.Symbols.CryptoSymbols, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Main execution loop — run this in a hosted service background task.
    /// </summary>
    public virtual async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExitManager starting with {interval}s check interval",
            _options.CheckIntervalSeconds);

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.CheckIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var exitSignals = await CheckPositionsAsync(stoppingToken);
                    foreach (var signal in exitSignals)
                    {
                        // Publish FIRST, then set PendingExit so a bus failure doesn't phantom-lock position
                        _ = await eventBus.PublishAsync(signal, stoppingToken);

                        var pos = positionTracker.GetPosition(signal.Symbol);
                        if (pos != null)
                        {
                            pos.PendingExit = true;
                            await RecordExitAttemptAsync(signal.Symbol, stoppingToken);
                        }

                        logger.LogInformation(
                            "Published exit signal: {symbol} {reason} @ {price}",
                            signal.Symbol, signal.ExitReason, signal.ExitPrice);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in exit check cycle");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ExitManager stopped");
        }
    }

    /// <summary>
    /// Checks all positions for exit conditions.
    ///
    /// Skips positions with invalid ATR (cannot safely compute risk levels).
    /// ATR-based rules take full priority: when ATR is valid, fixed-% rules (3/5) are not evaluated.
    /// PendingExit is NOT set here — the caller (ExecuteAsync) sets it after successful publish.
    /// </summary>
    public async ValueTask<List<ExitSignalEvent>> CheckPositionsAsync(CancellationToken ct)
    {
        var signals = new List<ExitSignalEvent>();

        var clock = await brokerService.GetClockAsync(ct);
        var positions = positionTracker.GetAllPositions();

        foreach (var (symbol, posData) in positions)
        {
            try
            {
                // Skip check if market closed (except 24/5 crypto)
                if (!clock.IsOpen && !IsCrypto24h(symbol))
                {
                    continue;
                }

                // Skip if pending exit or in backoff
                var backoffSeconds = await stateRepository.GetExitBackoffSecondsAsync(symbol, ct);
                if (posData.PendingExit && backoffSeconds > 0)
                {
                    continue;
                }

                // Skip position if ATR is invalid — cannot safely compute risk levels
                if (posData.AtrValue <= 0 || !ValidateAtr(posData.AtrValue))
                {
                    logger.LogWarning(
                        "Invalid ATR for {symbol}: {atr}, skipping exit checks",
                        symbol, posData.AtrValue);
                    continue;
                }

                var currentPrice = await GetCurrentPriceAsync(symbol, ct);
                if (currentPrice <= 0)
                {
                    continue;
                }

                // ATR levels are valid — compute once (atr_computed = true).
                // Fixed-% fallbacks (Rules 3 & 5) are mutually excluded when ATR is valid.
                var atrStop = posData.EntryPrice - (posData.AtrValue * _options.AtrStopLossMultiplier);
                var atrTarget = posData.EntryPrice + (posData.AtrValue * _options.AtrProfitTargetMultiplier);

                // Rule 1: ATR-based stop loss (highest priority)
                if (currentPrice <= atrStop)
                {
                    signals.Add(new ExitSignalEvent(symbol, "ATR_STOP_LOSS", currentPrice, DateTimeOffset.UtcNow));
                    continue;
                }

                // Rule 2: ATR-based profit target
                if (currentPrice >= atrTarget)
                {
                    signals.Add(new ExitSignalEvent(symbol, "ATR_PROFIT_TARGET", currentPrice, DateTimeOffset.UtcNow));
                    continue;
                }

                // Rule 4: Trailing stop (independent of ATR — always active when set)
                if (posData.TrailingStopPrice > 0 && currentPrice <= posData.TrailingStopPrice)
                {
                    signals.Add(new ExitSignalEvent(symbol, "TRAILING_STOP", currentPrice, DateTimeOffset.UtcNow));
                    continue;
                }

                // Rules 3 & 5 (fixed-% stop/profit) intentionally omitted when ATR is valid.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error checking exit conditions for {symbol}", symbol);
            }
        }

        return signals;
    }

    /// <summary>
    /// Handles order update events: clears pending_exit flag on terminal failure.
    /// </summary>
    public async ValueTask HandleOrderUpdateAsync(OrderUpdateEvent orderUpdate, CancellationToken ct)
    {
        var terminalFailureStates = new[]
        {
            OrderState.Canceled,
            OrderState.Expired,
            OrderState.Rejected,
        };

        if (!terminalFailureStates.Contains(orderUpdate.Status))
        {
            return;
        }

        var positions = positionTracker.GetAllPositions();
        foreach (var (symbol, posData) in positions)
        {
            if (posData.PendingExit && symbol == orderUpdate.Symbol)
            {
                posData.PendingExit = false;
                await RecordExitAttemptFailureAsync(symbol, ct);
                logger.LogWarning(
                    "Cleared pending exit for {symbol} after order {status}",
                    symbol, orderUpdate.Status);
                break;
            }
        }
    }

    /// <summary>
    /// Returns true if the symbol trades 24/5 (crypto — exempt from market-hours check).
    /// Uses the configured CryptoSymbols list from TradingOptions.
    /// </summary>
    private bool IsCrypto24h(string symbol) => _cryptoSymbols.Contains(symbol);

    /// <summary>
    /// Validates ATR is a finite positive number.
    /// </summary>
    private static bool ValidateAtr(decimal atr) =>
        atr > 0 && atr != decimal.MinValue && atr != decimal.MaxValue;

    /// <summary>
    /// Gets current mid-price for symbol from market data snapshot.
    /// </summary>
    private async ValueTask<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var snapshot = await marketDataClient.GetSnapshotAsync(symbol, ct);
            return snapshot.MidPrice;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get current price for {symbol}", symbol);
            return 0m;
        }
    }

    private async ValueTask RecordExitAttemptAsync(string symbol, CancellationToken ct)
    {
        try
        {
            await stateRepository.RecordExitAttemptAsync(symbol, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record exit attempt for {symbol}", symbol);
        }
    }

    private async ValueTask RecordExitAttemptFailureAsync(string symbol, CancellationToken ct)
    {
        try
        {
            await stateRepository.RecordExitAttemptFailureAsync(symbol, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record exit failure for {symbol}", symbol);
        }
    }
}
