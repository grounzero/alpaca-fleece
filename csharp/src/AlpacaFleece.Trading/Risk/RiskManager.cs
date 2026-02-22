namespace AlpacaFleece.Trading.Risk;

/// <summary>
/// Risk manager with 3-tier checks: Safety, Risk, Filters.
/// TIER 1 (Safety): Kill switch, circuit breaker, market clock
/// TIER 2 (Risk): Daily loss limit, trade count, position limits
/// TIER 3 (Filter): Confidence, regime bars, time-of-day (soft skip)
///
/// Crypto symbols (from options.Symbols.CryptoSymbols) are exempt from market-hours checks.
/// </summary>
public sealed class RiskManager(
    IBrokerService broker,
    IStateRepository stateRepository,
    TradingOptions options,
    ILogger<RiskManager> logger,
    IMarketDataClient? marketDataClient = null) : IRiskManager
{
    private readonly TimeZoneInfo _timeZone = TimeZoneInfo.FindSystemTimeZoneById(options.Session.TimeZone);
    private readonly HashSet<string> _cryptoSymbols =
        new(options.Symbols.CryptoSymbols, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Checks if a signal should be allowed based on risk rules (3-tier).
    /// Throws RiskManagerException on SAFETY/RISK failures (hard block).
    /// Returns RiskCheckResult with AllowsSignal=false on FILTER failures (soft skip).
    /// </summary>
    public async ValueTask<RiskCheckResult> CheckSignalAsync(
        SignalEvent signal,
        CancellationToken ct = default)
    {
        try
        {
            // TIER 1: SAFETY checks (hard blocks, throw exception)
            var safetyResult = await CheckSafetyTierAsync(signal, ct);
            if (!safetyResult.AllowsSignal)
            {
                throw new RiskManagerException(safetyResult.Reason);
            }

            // TIER 2: RISK checks (hard blocks, throw exception)
            var riskResult = await CheckRiskTierAsync(signal, ct);
            if (!riskResult.AllowsSignal)
            {
                throw new RiskManagerException(riskResult.Reason);
            }

            // TIER 3: FILTER checks (soft skip, return false)
            var filterResult = await CheckFilterTierAsync(signal, ct);
            if (!filterResult.AllowsSignal)
            {
                logger.LogDebug("Signal rejected by filter tier: {reason}", filterResult.Reason);
                return filterResult;
            }

            logger.LogDebug("Signal approved for {symbol}", signal.Symbol);
            return new RiskCheckResult(
                AllowsSignal: true,
                Reason: "Passed all risk checks",
                RiskTier: "PASSED");
        }
        catch (RiskManagerException ex)
        {
            logger.LogWarning(ex, "Risk manager hard block: {message}", ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Risk check error for {symbol}", signal.Symbol);
            throw new RiskManagerException($"Risk check error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// TIER 1: Safety checks (kill switch, circuit breaker, market hours).
    /// </summary>
    private async ValueTask<RiskCheckResult> CheckSafetyTierAsync(
        SignalEvent signal,
        CancellationToken ct = default)
    {
        // Kill switch active
        if (options.Execution.KillSwitch)
        {
            return new RiskCheckResult(
                AllowsSignal: false,
                Reason: "Kill switch active",
                RiskTier: "SAFETY");
        }

        // Circuit breaker tripped
        var circuitBreakerCount = await stateRepository.GetCircuitBreakerCountAsync(ct);
        if (circuitBreakerCount >= 5)
        {
            return new RiskCheckResult(
                AllowsSignal: false,
                Reason: $"Circuit breaker tripped ({circuitBreakerCount} failures)",
                RiskTier: "SAFETY");
        }

        // Market hours check (crypto exempt — trades 24/5)
        if (!IsCrypto(signal.Symbol))
        {
            var clock = await broker.GetClockAsync(ct);
            if (!clock.IsOpen)
            {
                return new RiskCheckResult(
                    AllowsSignal: false,
                    Reason: "Market is closed",
                    RiskTier: "SAFETY");
            }
        }

        return new RiskCheckResult(
            AllowsSignal: true,
            Reason: "Safety tier passed",
            RiskTier: "SAFETY");
    }

    /// <summary>
    /// TIER 2: Risk checks (daily loss limit, trade count, position limits).
    /// </summary>
    private async ValueTask<RiskCheckResult> CheckRiskTierAsync(
        SignalEvent signal,
        CancellationToken ct = default)
    {
        var account = await broker.GetAccountAsync(ct);

        // Daily PnL limit
        var dailyLossState = await stateRepository.GetStateAsync("daily_realized_pnl", ct);
        if (decimal.TryParse(dailyLossState ?? "0", out var dailyPnl))
        {
            if (dailyPnl < -options.RiskLimits.MaxDailyLoss)
            {
                return new RiskCheckResult(
                    AllowsSignal: false,
                    Reason: $"Daily loss limit exceeded: {dailyPnl:F2} vs {-options.RiskLimits.MaxDailyLoss}",
                    RiskTier: "RISK");
            }
        }

        // Daily trade count limit
        var tradeCountState = await stateRepository.GetStateAsync("daily_trade_count", ct);
        if (int.TryParse(tradeCountState ?? "0", out var tradeCount))
        {
            if (tradeCount >= options.RiskLimits.MaxTradesPerDay)
            {
                return new RiskCheckResult(
                    AllowsSignal: false,
                    Reason: $"Daily trade limit exceeded: {tradeCount} >= {options.RiskLimits.MaxTradesPerDay}",
                    RiskTier: "RISK");
            }
        }

        // Max concurrent positions check
        var positions = await broker.GetPositionsAsync(ct);
        if (positions.Count >= options.RiskLimits.MaxConcurrentPositions)
        {
            // Allow if this is a reversal (closing old, opening new)
            if (!positions.Any(p => p.Symbol == signal.Symbol))
            {
                return new RiskCheckResult(
                    AllowsSignal: false,
                    Reason: $"Max concurrent positions reached: {positions.Count} >= {options.RiskLimits.MaxConcurrentPositions}",
                    RiskTier: "RISK");
            }
        }

        // Max position size check
        const decimal defaultMaxPositionPct = 0.05m; // 5% of account per position
        var maxQty = (account.PortfolioValue * defaultMaxPositionPct) / signal.Metadata.CurrentPrice;
        if (maxQty < 1)
        {
            return new RiskCheckResult(
                AllowsSignal: false,
                Reason: $"Position size too small for account: max_qty={maxQty:F2}",
                RiskTier: "RISK");
        }

        return new RiskCheckResult(
            AllowsSignal: true,
            Reason: "Risk tier passed",
            RiskTier: "RISK");
    }

    /// <summary>
    /// TIER 3: Filter checks (confidence, regime bars, time-of-day, spread).
    /// Returns false on failure (soft skip, not an exception).
    /// </summary>
    private async ValueTask<RiskCheckResult> CheckFilterTierAsync(
        SignalEvent signal,
        CancellationToken ct = default)
    {
        // Confidence filter: reject low-quality signals
        var minConfidence = options.RiskLimits.MinSignalConfidence;
        if (signal.Metadata.Confidence < minConfidence)
        {
            return new RiskCheckResult(
                AllowsSignal: false,
                Reason: $"Signal confidence too low: {signal.Metadata.Confidence:F2} < {minConfidence:F2}",
                RiskTier: "FILTER");
        }

        // Bar volume / regime filter
        if (signal.Metadata.BarsInRegime == 0 || signal.Metadata.BarsInRegime < 10)
        {
            return new RiskCheckResult(
                AllowsSignal: false,
                Reason: $"Insufficient regime bars: {signal.Metadata.BarsInRegime}",
                RiskTier: "FILTER");
        }

        // Time of day filter (skip first N minutes after open and last M minutes before close)
        if (!IsCrypto(signal.Symbol))
        {
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).TimeOfDay;
            var openTime = options.Session.MarketOpenTime;
            var closeTime = options.Session.MarketCloseTime;

            // Too soon after open
            var minutesAfterOpen = (now - openTime).TotalMinutes;
            if (minutesAfterOpen < options.Filters.MinMinutesAfterOpen)
            {
                return new RiskCheckResult(
                    AllowsSignal: false,
                    Reason: $"Too soon after market open: {minutesAfterOpen:F1} min < {options.Filters.MinMinutesAfterOpen} min",
                    RiskTier: "FILTER");
            }

            // Too close to close
            var minutesBeforeClose = (closeTime - now).TotalMinutes;
            if (minutesBeforeClose < options.Filters.MinMinutesBeforeClose)
            {
                return new RiskCheckResult(
                    AllowsSignal: false,
                    Reason: $"Too close to market close: {minutesBeforeClose:F1} min < {options.Filters.MinMinutesBeforeClose} min",
                    RiskTier: "FILTER");
            }
        }

        // Spread filter: skip if bid/ask spread is too wide
        if (marketDataClient != null)
        {
            try
            {
                var snapshot = await marketDataClient.GetSnapshotAsync(signal.Symbol, ct);
                if (snapshot != null && snapshot.Bid > 0 && snapshot.Ask > 0)
                {
                    var spread = (snapshot.Ask - snapshot.Bid) / snapshot.Bid;
                    if (spread > options.Filters.MaxSpreadPct)
                    {
                        return new RiskCheckResult(
                            AllowsSignal: false,
                            Reason: $"Spread too wide: {spread:P2} > {options.Filters.MaxSpreadPct:P2}",
                            RiskTier: "FILTER");
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check spread for {symbol}, skipping spread filter", signal.Symbol);
            }
        }

        return new RiskCheckResult(
            AllowsSignal: true,
            Reason: "Filter tier passed",
            RiskTier: "FILTER");
    }

    /// <summary>
    /// Returns true if the symbol is a crypto asset (trades 24/5, exempt from market-hours checks).
    /// Uses the configured CryptoSymbols list from TradingOptions.
    /// </summary>
    private bool IsCrypto(string symbol) => _cryptoSymbols.Contains(symbol);
}
