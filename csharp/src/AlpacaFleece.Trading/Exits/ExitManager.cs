namespace AlpacaFleece.Trading.Exits;

/// <summary>
/// Exit manager: checks positions every 30s for stop loss, trailing stop, profit target.
/// Uses 5-rule priority system with ATR-based dynamic levels.
/// Publishes ExitSignalEvent to unbounded event bus (never dropped).
/// </summary>
public class ExitManager(
    IPositionTracker positionTracker,
    IBrokerService brokerService,
    IMarketDataClient marketDataClient,
    IEventBus eventBus,
    IStateRepository stateRepository,
    ILogger<ExitManager> logger,
    IOptions<ExitOptions> options)
{
    // Protected no-arg constructor for NSubstitute proxy creation
    protected ExitManager() : this(null!, null!, null!, null!, null!, null!, Options.Create(new ExitOptions())) { }

    private readonly ExitOptions _options = options.Value;

    /// <summary>
    /// Main execution loop - run this in a hosted service background task.
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
                        _ = await eventBus.PublishAsync(signal, stoppingToken);
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
        finally
        {
            timer.Dispose();
        }
    }

    /// <summary>
    /// Checks all positions for exit conditions (5-rule priority).
    /// Priority: stop loss → trailing stop → profit target.
    /// </summary>
    public async ValueTask<List<ExitSignalEvent>> CheckPositionsAsync(CancellationToken ct)
    {
        var signals = new List<ExitSignalEvent>();

        // Get current market clock
        var clock = await brokerService.GetClockAsync(ct);
        var positions = positionTracker.GetAllPositions();

        foreach (var (symbol, posData) in positions)
        {
            try
            {
                // Skip check if market closed (except crypto 24/5)
                if (!clock.IsOpen && !IsCrypto24h(symbol))
                {
                    continue;
                }

                // Skip if pending exit or in backoff
                var backoffSeconds = await stateRepository.GetExitBackoffSecondsAsync(
                    symbol, ct);
                if (posData.PendingExit && backoffSeconds > 0)
                {
                    continue;
                }

                // Skip position if ATR is invalid (cannot safely compute risk levels)
                if (posData.AtrValue <= 0 || !ValidateAtr(posData.AtrValue))
                {
                    logger.LogWarning("Invalid ATR for {symbol}: {atr}, skipping exit checks", symbol, posData.AtrValue);
                    continue;
                }

                // Get current price (simplified: would fetch from market data)
                var currentPrice = await GetCurrentPriceAsync(symbol, ct);
                if (currentPrice <= 0)
                {
                    continue;
                }

                // Calculate P&L percentage
                var pnlPct = (currentPrice - posData.EntryPrice) / posData.EntryPrice;

                // Rule 1: ATR-based stop loss
                if (posData.AtrValue > 0 && ValidateAtr(posData.AtrValue))
                {
                    var atrStop = posData.EntryPrice - (posData.AtrValue * _options.AtrStopLossMultiplier);
                    if (currentPrice <= atrStop)
                    {
                        signals.Add(new ExitSignalEvent(
                            symbol, "ATR_STOP_LOSS", currentPrice, DateTimeOffset.UtcNow));
                        posData.PendingExit = true;
                        await RecordExitAttemptAsync(symbol, ct);
                        continue;
                    }
                }

                // Rule 2: ATR-based profit target
                if (posData.AtrValue > 0 && ValidateAtr(posData.AtrValue))
                {
                    var atrTarget = posData.EntryPrice + (posData.AtrValue * _options.AtrProfitTargetMultiplier);
                    if (currentPrice >= atrTarget)
                    {
                        signals.Add(new ExitSignalEvent(
                            symbol, "ATR_PROFIT_TARGET", currentPrice, DateTimeOffset.UtcNow));
                        posData.PendingExit = true;
                        await RecordExitAttemptAsync(symbol, ct);
                        continue;
                    }
                }

                // Rule 3: Fixed % stop loss
                if (pnlPct <= -_options.StopLossPercentage)
                {
                    signals.Add(new ExitSignalEvent(
                        symbol, "FIXED_STOP_LOSS", currentPrice, DateTimeOffset.UtcNow));
                    posData.PendingExit = true;
                    await RecordExitAttemptAsync(symbol, ct);
                    continue;
                }

                // Rule 4: Trailing stop
                if (posData.TrailingStopPrice > 0 && currentPrice <= posData.TrailingStopPrice)
                {
                    signals.Add(new ExitSignalEvent(
                        symbol, "TRAILING_STOP", currentPrice, DateTimeOffset.UtcNow));
                    posData.PendingExit = true;
                    await RecordExitAttemptAsync(symbol, ct);
                    continue;
                }

                // Rule 5: Fixed % profit target
                if (pnlPct >= _options.ProfitTargetPercentage)
                {
                    signals.Add(new ExitSignalEvent(
                        symbol, "FIXED_PROFIT_TARGET", currentPrice, DateTimeOffset.UtcNow));
                    posData.PendingExit = true;
                    await RecordExitAttemptAsync(symbol, ct);
                    continue;
                }
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
        // Check if terminal failure
        var terminalFailureStates = new[]
        {
            OrderState.Canceled,
            OrderState.Expired,
            OrderState.Rejected,
            OrderState.PartiallyFilled
        };

        if (!terminalFailureStates.Contains(orderUpdate.Status))
        {
            return;
        }

        // Find position by symbol (extract from order or metadata)
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
    /// Validates ATR is a finite positive number.
    /// </summary>
    private static bool ValidateAtr(decimal atr)
    {
        // Check for positive and not NaN
        return atr > 0 && atr != decimal.MinValue && atr != decimal.MaxValue;
    }

    /// <summary>
    /// Checks if symbol is 24h crypto (simplified).
    /// </summary>
    private static bool IsCrypto24h(string symbol)
    {
        // In production, check against known crypto symbols or market category
        return symbol.EndsWith("USDT") || symbol.EndsWith("BUSD");
    }

    /// <summary>
    /// Gets current price for symbol from market data snapshot.
    /// Uses mid-price from bid/ask spread.
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

    /// <summary>
    /// Records exit attempt in exit_attempts table (for backoff tracking).
    /// </summary>
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

    /// <summary>
    /// Records failed exit attempt for backoff calculation.
    /// </summary>
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
