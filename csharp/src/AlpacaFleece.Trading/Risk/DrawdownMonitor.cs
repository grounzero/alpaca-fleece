namespace AlpacaFleece.Trading.Risk;

/// <summary>
/// Tracks peak-to-trough portfolio drawdown and manages escalation levels.
///
/// State machine (Normal → Warning → Halt → Emergency):
///   Normal    — drawdown < WarningThresholdPct;  full trading
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
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// Initializes the drawdown monitor by loading persisted state from database.
    /// Must be called before using GetCurrentLevel() to ensure accurate state after restarts.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (!options.Drawdown.Enabled)
        {
            logger.LogInformation("DrawdownMonitor: disabled, skipping initialization");
            return;
        }

        try
        {
            var state = await stateRepository.GetDrawdownStateAsync(ct);
            if (state != null)
            {
                _currentLevel = state.Level;
                logger.LogInformation(
                    "DrawdownMonitor initialized from database: level={level}, peak={peak:F2}, drawdown={drawdown:P2}",
                    state.Level, state.PeakEquity, state.DrawdownPct);
            }
            else
            {
                logger.LogInformation("DrawdownMonitor: no persisted state found, starting at Normal");
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DrawdownMonitor: failed to initialize from database, defaulting to Normal");
            _currentLevel = DrawdownLevel.Normal;
        }
    }

    /// <summary>
    /// Returns the current drawdown level (synchronous, hot-path safe).
    /// </summary>
    public DrawdownLevel GetCurrentLevel() => _currentLevel;

    /// <summary>
    /// Returns the position size multiplier for the current drawdown level.
    /// WarningPositionMultiplier during Warning, 1.0 for all other levels.
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
    ///
    /// On repeated broker/DB failures, escalates to Halt after MaxConsecutiveFailures attempts
    /// to ensure fail-safe behavior.
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

            // Reset failure counter on success
            _consecutiveFailures = 0;

            // Persist state BEFORE updating in-memory to ensure consistency
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
            _consecutiveFailures++;
            logger.LogError(ex, 
                "DrawdownMonitor: failed to update drawdown state (failure {count}/{max})",
                _consecutiveFailures, MaxConsecutiveFailures);

            // Fail-safe: escalate to Halt after repeated failures
            if (_consecutiveFailures >= MaxConsecutiveFailures)
            {
                var previousLevel = _currentLevel;

                // Only escalate from less severe states (Normal/Warning) to Halt.
                // Do NOT downgrade from Emergency or re-escalate from Halt.
                if (previousLevel == DrawdownLevel.Normal || previousLevel == DrawdownLevel.Warning)
                {
                    _currentLevel = DrawdownLevel.Halt;
                    logger.LogCritical(
                        "DrawdownMonitor: fail-safe triggered after {count} failures, escalating from {previous} to HALT",
                        _consecutiveFailures, previousLevel);
                    return (previousLevel, DrawdownLevel.Halt, 0m);
                }

                // Already in Halt or Emergency: remain at current level but still log critical fail-safe.
                logger.LogCritical(
                    "DrawdownMonitor: fail-safe triggered after {count} failures at level {level}, no further escalation",
                    _consecutiveFailures, _currentLevel);
            }

            // Return current state without change on transient failure
            return (_currentLevel, _currentLevel, 0m);
        }
    }
}
