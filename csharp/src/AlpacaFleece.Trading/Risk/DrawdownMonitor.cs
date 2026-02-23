namespace AlpacaFleece.Trading.Risk;

/// <summary>
/// Tracks peak-to-trough portfolio drawdown and manages escalation/recovery levels.
///
/// State machine (Normal ⟷ Warning ⟷ Halt ⟷ Emergency):
///   Normal    — drawdown < WarningThresholdPct;  full trading
///   Warning   — drawdown ≥ WarningThresholdPct;  position sizes × WarningPositionMultiplier
///   Halt      — drawdown ≥ HaltThresholdPct;     no new positions
///   Emergency — drawdown ≥ EmergencyThresholdPct; all positions closed
///
/// Recovery (when EnableAutoRecovery is true):
///   Emergency → Halt   — when drawdown < EmergencyRecoveryThresholdPct
///   Halt → Warning     — when drawdown < HaltRecoveryThresholdPct
///   Warning → Normal   — when drawdown < WarningRecoveryThresholdPct
///   Otherwise requires system restart with manual recovery flag
///
/// Rolling peak window: Peak resets to current equity at start of each lookback period (default 20 days).
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
    private DateTimeOffset _lastPeakResetTime = DateTimeOffset.UtcNow;
    private int _consecutiveFailures = 0;
    private const int MaxConsecutiveFailures = 3;

    /// <summary>
    /// Initialises the drawdown monitor by loading persisted state from database.
    /// Handles manual recovery flag and rolling window reset.
    /// Must be called before using GetCurrentLevel() to ensure accurate state after restarts.
    /// </summary>
    public async Task InitialiseAsync(CancellationToken ct = default)
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
                _lastPeakResetTime = state.LastPeakResetTime;

                // Handle manual recovery flag (if in manual mode and flag is set)
                if (!options.Drawdown.EnableAutoRecovery && state.ManualRecoveryRequested)
                {
                    logger.LogInformation(
                        "DrawdownMonitor: manual recovery requested, resetting to Normal at startup");
                    _currentLevel = DrawdownLevel.Normal;
                    // Clear the flag by persisting with ManualRecoveryRequested=false
                    await stateRepository.SaveDrawdownStateAsync(
                        DrawdownLevel.Normal, state.PeakEquity, state.CurrentDrawdownPct,
                        _lastPeakResetTime, manualRecoveryRequested: false, ct);
                }
                else
                {
                    _currentLevel = state.Level;
                }

                logger.LogInformation(
                    "DrawdownMonitor initialized from database: level={level}, peak={peak:F2}, drawdown={drawdown:P2}, lookbackResetTime={resetTime:O}",
                    _currentLevel, state.PeakEquity, state.CurrentDrawdownPct, _lastPeakResetTime);
            }
            else
            {
                logger.LogInformation("DrawdownMonitor: no persisted state found, starting at Normal");
                _lastPeakResetTime = DateTimeOffset.UtcNow;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DrawdownMonitor: failed to initialize from database, defaulting to Normal");
            _currentLevel = DrawdownLevel.Normal;
            _lastPeakResetTime = DateTimeOffset.UtcNow;
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
    /// Rolling lookback: Peak resets to current equity at start of each lookback window (LookbackDays).
    ///
    /// Level transitions:
    /// - Escalation: Normal → Warning → Halt → Emergency (based on escalation thresholds)
    /// - Recovery: Emergency → Halt → Warning → Normal (based on recovery thresholds, if EnableAutoRecovery)
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

            // Load persisted peak
            var state = await stateRepository.GetDrawdownStateAsync(ct);
            _lastPeakResetTime = state?.LastPeakResetTime ?? DateTimeOffset.UtcNow;

            // Check if rolling lookback window has expired
            var lookbackDays = options.Drawdown.LookbackDays;
            var windowExpired = DateTimeOffset.UtcNow - _lastPeakResetTime > TimeSpan.FromDays(lookbackDays);

            var peakEquity = state is { PeakEquity: > 0 } && !windowExpired
                ? Math.Max(state.PeakEquity, currentEquity)
                : currentEquity;

            // Reset peak and time if window expired
            if (windowExpired)
            {
                _lastPeakResetTime = DateTimeOffset.UtcNow;
                logger.LogInformation(
                    "DrawdownMonitor: rolling lookback window ({days} days) expired, resetting peak from {oldPeak:F2} to current equity {newPeak:F2}",
                    lookbackDays, state?.PeakEquity ?? 0m, currentEquity);
            }

            var drawdownPct = peakEquity > 0
                ? (peakEquity - currentEquity) / peakEquity
                : 0m;

            var cfg = options.Drawdown;
            var previousLevel = _currentLevel;

            // Determine new level with hysteresis-based recovery thresholds
            var newLevel = CalculateLevel(previousLevel, drawdownPct, cfg);

            // Reset failure counter on success
            Interlocked.Exchange(ref _consecutiveFailures, 0);

            // Persist state BEFORE updating in-memory to ensure consistency
            await stateRepository.SaveDrawdownStateAsync(
                newLevel, peakEquity, drawdownPct, _lastPeakResetTime,
                manualRecoveryRequested: state?.ManualRecoveryRequested ?? false, ct);
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
            var failureCount = Interlocked.Increment(ref _consecutiveFailures);
            logger.LogError(ex,
                "DrawdownMonitor: failed to update drawdown state (failure {count}/{max})",
                failureCount, MaxConsecutiveFailures);

            // Fail-safe: escalate to Halt after repeated failures
            if (failureCount >= MaxConsecutiveFailures)
            {
                var previousLevel = _currentLevel;

                // Only escalate from less severe states (Normal/Warning) to Halt.
                // Do NOT downgrade from Emergency or re-escalate from Halt.
                if (previousLevel == DrawdownLevel.Normal || previousLevel == DrawdownLevel.Warning)
                {
                    _currentLevel = DrawdownLevel.Halt;
                    logger.LogCritical(
                        "DrawdownMonitor: fail-safe triggered after {count} failures, escalating from {previous} to HALT",
                        failureCount, previousLevel);
                    return (previousLevel, DrawdownLevel.Halt, 0m);
                }

                // Already in Halt or Emergency: remain at current level but still log critical fail-safe.
                logger.LogCritical(
                    "DrawdownMonitor: fail-safe triggered after {count} failures at level {level}, no further escalation",
                    failureCount, _currentLevel);
            }

            // Return current state without change on transient failure
            return (_currentLevel, _currentLevel, 0m);
        }
    }

    /// <summary>
    /// Calculates the new drawdown level based on current level, drawdown%, and thresholds.
    /// Implements hysteresis: escalates at higher threshold, recovers at lower threshold (if auto-recovery enabled).
    /// </summary>
    private DrawdownLevel CalculateLevel(DrawdownLevel currentLevel, decimal drawdownPct, DrawdownOptions cfg)
    {
        // Check escalation thresholds first (highest priority)
        if (drawdownPct >= cfg.EmergencyThresholdPct)
            return DrawdownLevel.Emergency;
        if (drawdownPct >= cfg.HaltThresholdPct)
            return DrawdownLevel.Halt;
        if (drawdownPct >= cfg.WarningThresholdPct)
            return DrawdownLevel.Warning;

        // Check recovery thresholds (only if auto-recovery is enabled)
        if (!cfg.EnableAutoRecovery)
            return currentLevel; // No recovery if disabled

        // Recovery logic: descend levels when drawdown falls below recovery thresholds
        return currentLevel switch
        {
            DrawdownLevel.Emergency when drawdownPct < cfg.EmergencyRecoveryThresholdPct
                => DrawdownLevel.Halt,

            DrawdownLevel.Halt when drawdownPct < cfg.HaltRecoveryThresholdPct
                => DrawdownLevel.Warning,

            DrawdownLevel.Warning when drawdownPct < cfg.WarningRecoveryThresholdPct
                => DrawdownLevel.Normal,

            _ => currentLevel
        };
    }
}
