namespace AlpacaFleece.Trading.Risk;

/// <summary>
/// Tracks peak-to-trough portfolio drawdown and manages escalation levels.
///
/// State machine (Normal → Warning → Halt → Emergency):
///   Normal    — drawdown &lt; WarningThresholdPct;  full trading
///   Warning   — drawdown ≥ WarningThresholdPct;  position sizes × WarningPositionMultiplier
///   Halt      — drawdown ≥ HaltThresholdPct;     no new positions
///   Emergency — drawdown ≥ EmergencyThresholdPct; all positions closed
///
/// UpdateAsync() is called periodically (DrawdownMonitorService). The current level is cached
/// in-memory (volatile) so RiskManager and OrderManager can read it synchronously on hot paths.
/// </summary>
public sealed class DrawdownMonitor(
    IBrokerService broker,
    IStateRepository stateRepository,
    TradingOptions options,
    ILogger<DrawdownMonitor> logger)
{
    private volatile DrawdownLevel _currentLevel = DrawdownLevel.Normal;

    /// <summary>
    /// Returns the current drawdown level (synchronous, hot-path safe).
    /// </summary>
    public DrawdownLevel GetCurrentLevel() => _currentLevel;

    /// <summary>
    /// Returns the position size multiplier for the current drawdown level.
    /// 0.5 during Warning, 1.0 for all other levels.
    /// </summary>
    public decimal GetPositionMultiplier() =>
        _currentLevel == DrawdownLevel.Warning
            ? options.Drawdown.WarningPositionMultiplier
            : 1.0m;

    /// <summary>
    /// Fetches current account equity, recalculates drawdown vs rolling peak, persists state,
    /// and returns the (previous, current, drawdownPct) transition tuple.
    ///
    /// If Drawdown.Enabled is false, returns (Normal, Normal, 0) without touching the broker or DB.
    /// </summary>
    public async ValueTask<(DrawdownLevel Previous, DrawdownLevel Current, decimal DrawdownPct)>
        UpdateAsync(CancellationToken ct = default)
    {
        if (!options.Drawdown.Enabled)
            return (DrawdownLevel.Normal, DrawdownLevel.Normal, 0m);

        try
        {
            var account = await broker.GetAccountAsync(ct);
            var currentEquity = account.PortfolioValue;

            // Load persisted peak; initialise to current equity on first run
            var state = await stateRepository.GetDrawdownStateAsync(ct);
            var peakEquity = state is { PeakEquity: > 0 }
                ? Math.Max(state.PeakEquity, currentEquity)
                : currentEquity;

            var drawdownPct = peakEquity > 0
                ? (peakEquity - currentEquity) / peakEquity
                : 0m;

            var cfg = options.Drawdown;
            var newLevel = drawdownPct >= cfg.EmergencyThresholdPct ? DrawdownLevel.Emergency
                : drawdownPct >= cfg.HaltThresholdPct ? DrawdownLevel.Halt
                : drawdownPct >= cfg.WarningThresholdPct ? DrawdownLevel.Warning
                : DrawdownLevel.Normal;

            var previousLevel = _currentLevel;

            await stateRepository.SaveDrawdownStateAsync(newLevel, peakEquity, drawdownPct, ct);
            _currentLevel = newLevel;

            if (previousLevel != newLevel)
            {
                logger.LogWarning(
                    "Drawdown level transition: {previous} → {current} (drawdown={pct:P2}, peak={peak:F2})",
                    previousLevel, newLevel, drawdownPct, peakEquity);
            }
            else
            {
                logger.LogDebug(
                    "Drawdown check: {level} (drawdown={pct:P2}, equity={equity:F2}, peak={peak:F2})",
                    newLevel, drawdownPct, currentEquity, peakEquity);
            }

            return (previousLevel, newLevel, drawdownPct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DrawdownMonitor: failed to update drawdown state");
            return (_currentLevel, _currentLevel, 0m);
        }
    }
}
