using AlpacaFleece.Infrastructure.Symbols;

namespace AlpacaFleece.Trading.Exits;

/// <summary>
/// Exit manager: checks positions periodically for stop loss, trailing stop, profit target.
/// Uses a 3-rule priority system with ATR-based dynamic levels (mutual exclusion with fixed-%):
///
///   Rule 1: ATR stop loss     — entry - (ATR x AtrStopLossMultiplier)
///   Rule 2: ATR profit target — entry + (ATR x AtrProfitTargetMultiplier)
///   Rule 3: Trailing stop     — TrailingStopPrice (always active)
///
/// ATR mutual exclusion: when valid ATR levels exist, fixed-percentage rules are skipped.
/// Publishes ExitSignalEvent to unbounded event bus (never dropped).
/// PendingExit flag is set AFTER successful publish to avoid phantom locks on bus failures.
/// </summary>
/// <param name="positionTracker">The position tracker for accessing open positions.</param>
/// <param name="brokerService">The broker service for executing exit trades.</param>
/// <param name="marketDataClient">The market data client for fetching prices.</param>
/// <param name="eventBus">The event bus for publishing exit signals.</param>
/// <param name="stateRepository">The state repository for storing exit attempt records.</param>
/// <param name="logger">The logger instance.</param>
/// <param name="options">The trading options configuration.</param>
/// <param name="symbolClassifier">Optional symbol classifier for crypto/equity detection.</param>
/// <param name="volatilityRegimeDetector">Optional volatility regime detector for ATR distance adaptation.</param>
public class ExitManager(
    IPositionTracker positionTracker,
    IBrokerService brokerService,
    IMarketDataClient marketDataClient,
    IEventBus eventBus,
    IStateRepository stateRepository,
    ILogger<ExitManager> logger,
    IOptions<TradingOptions> options,
    ISymbolClassifier? symbolClassifier = null,
    VolatilityRegimeDetector? volatilityRegimeDetector = null)
{
    /// <summary>
    /// Protected no-arg constructor for NSubstitute proxy creation in testing.
    /// </summary>
    protected ExitManager() : this(null!, null!, null!, null!, null!, null!, Options.Create(new TradingOptions()), null!, null!) { }

    private readonly ExitOptions _options = options.Value.Exit;
    private readonly ISymbolClassifier _symbolClassifier = symbolClassifier ?? new SymbolClassifier(options.Value.Symbols.CryptoSymbols, options.Value.Symbols.EquitySymbols);

    // Per-symbol consecutive price-fetch failure counter.
    // After MaxPriceFailures consecutive failures the market_data_degraded KV is set,
    // which blocks new entries in RiskManager (SAFETY tier).
    private readonly Dictionary<string, int> _priceFetchFailures = new();
    private const int MaxPriceFailures = 3;

    // R-3: Cache the market clock to avoid an Alpaca API call on every 30-second exit check cycle.
    // The clock is refreshed at most once per minute (60s TTL).
    private ClockInfo? _cachedClock;
    private DateTimeOffset _clockCacheTime = DateTimeOffset.MinValue;
    private static readonly TimeSpan ClockCacheTtl = TimeSpan.FromSeconds(60);

    // Scaling exits: track high water mark per symbol for trailing stop tiers
    private readonly Dictionary<string, decimal> _highWaterMarks = new();

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

        // R-3: Use cached clock if fresh (< 60s); otherwise fetch and cache a new one.
        // Reduces Alpaca clock API calls from once per 30s cycle to at most once per minute.
        ClockInfo clock;
        var now = DateTimeOffset.UtcNow;
        if (_cachedClock != null && now - _clockCacheTime < ClockCacheTtl)
        {
            clock = _cachedClock;
        }
        else
        {
            clock = await brokerService.GetClockAsync(ct);
            _cachedClock = clock;
            _clockCacheTime = now;
        }

        var positions = positionTracker.GetAllPositions();

        foreach (var (symbol, posData) in positions)
        {
            try
            {
                // Skip check if market closed (except 24/5 crypto)
                if (!clock.IsOpen && !_symbolClassifier.IsCrypto(symbol))
                {
                    continue;
                }

                // Skip if pending exit (waiting for order to fill/fail or in backoff period)
                if (posData.PendingExit)
                {
                    var nextRetryAt = await stateRepository.GetExitAttemptNextRetryAtAsync(symbol, ct);
                    // If no backoff recorded (null), order is still pending; keep waiting
                    // If backoff is recorded but not expired, keep waiting
                    if (nextRetryAt == null || now < nextRetryAt.Value)
                    {
                        continue; // Still waiting (no failure backoff, or backoff not yet expired)
                    }
                    // Backoff has expired; allow retry by clearing PendingExit
                    posData.PendingExit = false;
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

                var atrDistanceMultiplier = 1.0m;
                if (volatilityRegimeDetector is { Enabled: true })
                {
                    var volRegime = await volatilityRegimeDetector.GetRegimeAsync(symbol, ct);
                    atrDistanceMultiplier = volRegime.StopMultiplier;
                    logger.LogDebug(
                        "Volatility stops for {symbol}: regime={regime} vol={vol:F6} bars={bars} stopMult={mult:F2}",
                        symbol, volRegime.Regime, volRegime.RealisedVolatility, volRegime.BarsInRegime, atrDistanceMultiplier);
                }

                // ATR levels are valid — compute once (atr_computed = true).
                // Fixed-% fallbacks (Rules 3 & 5) are mutually excluded when ATR is valid.
                var atrStop = posData.EntryPrice - (posData.AtrValue * _options.AtrStopLossMultiplier * atrDistanceMultiplier);
                var atrTarget = posData.EntryPrice + (posData.AtrValue * _options.AtrProfitTargetMultiplier * atrDistanceMultiplier);

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

                // Scaling exits: check for partial profit-taking at multiple tiers
                if (_options.ScalingExits.Count > 0)
                {
                    var scalingExits = await CheckScalingExitsAsync(symbol, posData, currentPrice, ct);
                    foreach (var (signal, _) in scalingExits)
                    {
                        signals.Add(signal);
                    }
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
    /// Validates ATR is a finite positive number.
    /// </summary>
    private static bool ValidateAtr(decimal atr) =>
        atr > 0 && atr != decimal.MinValue && atr != decimal.MaxValue;

    /// <summary>
    /// Gets current mid-price for symbol from market data snapshot.
    /// Tracks consecutive failures per symbol; after MaxPriceFailures consecutive failures
    /// the market_data_degraded KV flag is set (blocks new entries in RiskManager SAFETY tier).
    /// On success, clears that symbol's failure counter and clears the KV flag when all symbols OK.
    /// Returns 0m on individual failure so the position is skipped this cycle.
    /// </summary>
    private async ValueTask<decimal> GetCurrentPriceAsync(string symbol, CancellationToken ct)
    {
        try
        {
            var snapshot = await marketDataClient.GetSnapshotAsync(symbol, ct);

            // Success: reset this symbol's failure counter.
            if (_priceFetchFailures.TryGetValue(symbol, out var prev) && prev > 0)
            {
                _priceFetchFailures[symbol] = 0;

                // Clear the degraded flag only when every *currently held* position has a
                // clean price feed. Counters for symbols that are no longer held are stale
                // and must not prevent recovery — prune them here.
                var currentSymbols = positionTracker.GetAllPositions().Keys.ToHashSet();
                foreach (var key in _priceFetchFailures.Keys.Where(k => !currentSymbols.Contains(k)).ToList())
                    _priceFetchFailures.Remove(key);

                if (_priceFetchFailures.Values.All(v => v == 0))
                {
                    await stateRepository.SetStateAsync("market_data_degraded", "false", ct);
                    logger.LogInformation("Market data recovered for all held positions; cleared market_data_degraded flag");
                }
            }

            return snapshot.MidPrice;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get current price for {symbol}", symbol);
            _priceFetchFailures.TryGetValue(symbol, out var failures);
            _priceFetchFailures[symbol] = failures + 1;

            if (_priceFetchFailures[symbol] >= MaxPriceFailures)
            {
                logger.LogError(
                    "Price fetch failed {count} consecutive times for {symbol}; setting market_data_degraded=true to block new entries",
                    _priceFetchFailures[symbol], symbol);
                await stateRepository.SetStateAsync("market_data_degraded", "true", ct);
            }

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

    /// <summary>
    /// Checks for scaling exit conditions (partial profit-taking at multiple tiers).
    /// Only runs if ScalingExits is configured and not empty.
    /// Returns list of scaling exit signals (each with quantity to close).
    /// </summary>
    private async ValueTask<List<(ExitSignalEvent Signal, decimal Quantity)>> CheckScalingExitsAsync(
        string symbol,
        PositionData posData,
        decimal currentPrice,
        CancellationToken ct)
    {
        var results = new List<(ExitSignalEvent, decimal)>();

        if (_options.ScalingExits.Count == 0)
            return results;

        try
        {
            // Update high water mark for this symbol
            if (!_highWaterMarks.TryGetValue(symbol, out var hwm) || currentPrice > hwm)
            {
                _highWaterMarks[symbol] = currentPrice;
            }

            var atrDistanceMultiplier = 1.0m;
            if (volatilityRegimeDetector is { Enabled: true })
            {
                var volRegime = await volatilityRegimeDetector.GetRegimeAsync(symbol, ct);
                atrDistanceMultiplier = volRegime.StopMultiplier;
            }

            foreach (var tier in _options.ScalingExits.OrderBy(t => t.DistanceMultiplier))
            {
                if (tier.Trigger.Equals("ProfitTarget", StringComparison.OrdinalIgnoreCase))
                {
                    var targetPrice = posData.EntryPrice + (posData.AtrValue * tier.DistanceMultiplier * atrDistanceMultiplier);
                    if (currentPrice >= targetPrice)
                    {
                        var qtyToClose = _symbolClassifier.IsCrypto(symbol)
                            ? Math.Max(0.0001m, Math.Round(posData.CurrentQuantity * tier.PercentageToClose, 8))
                            : Math.Max(1m, Math.Floor(posData.CurrentQuantity * tier.PercentageToClose));

                        if (qtyToClose > 0)
                        {
                            var signal = new ExitSignalEvent(
                                symbol,
                                $"SCALING_PROFIT_TIER_{tier.DistanceMultiplier:F1}x",
                                currentPrice,
                                DateTimeOffset.UtcNow);
                            results.Add((signal, qtyToClose));

                            logger.LogInformation(
                                "Scaling exit triggered: {symbol} SELL {qty} shares @ {price:F2} (tier {mult}x ATR, profit target)",
                                symbol, qtyToClose, currentPrice, tier.DistanceMultiplier);
                        }
                    }
                }
                else if (tier.Trigger.Equals("TrailingStop", StringComparison.OrdinalIgnoreCase))
                {
                    var hwPrice = _highWaterMarks.GetValueOrDefault(symbol, posData.EntryPrice);
                    var trailDistance = posData.AtrValue * tier.DistanceMultiplier * atrDistanceMultiplier;
                    var trailPrice = hwPrice - trailDistance;

                    if (currentPrice <= trailPrice)
                    {
                        var qtyToClose = _symbolClassifier.IsCrypto(symbol)
                            ? Math.Max(0.0001m, Math.Round(posData.CurrentQuantity * tier.PercentageToClose, 8))
                            : Math.Max(1m, Math.Floor(posData.CurrentQuantity * tier.PercentageToClose));

                        if (qtyToClose > 0)
                        {
                            var signal = new ExitSignalEvent(
                                symbol,
                                $"SCALING_TRAIL_TIER_{tier.DistanceMultiplier:F1}x",
                                currentPrice,
                                DateTimeOffset.UtcNow);
                            results.Add((signal, qtyToClose));

                            logger.LogInformation(
                                "Scaling exit triggered: {symbol} SELL {qty} shares @ {price:F2} (tier {mult}x ATR, trailing stop)",
                                symbol, qtyToClose, currentPrice, tier.DistanceMultiplier);
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error checking scaling exits for {symbol}", symbol);
        }

        return results;
    }
}
