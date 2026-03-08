namespace AlpacaFleece.Trading.Strategy;

/// <summary>
/// RSI mean-reversion strategy: emits a BUY signal when RSI crosses into oversold territory
/// (prevRsi ≥ OversoldThreshold &amp;&amp; currentRsi &lt; OversoldThreshold).
/// <para>
/// This strategy is the natural complement to <see cref="SmaCrossoverStrategy"/>:
/// where the SMA crossover targets trending markets, RSI Momentum targets ranging /
/// mean-reverting markets by buying oversold dips.
/// </para>
/// <para>
/// Signal logic (long-only):
/// <list type="bullet">
///   <item>BUY — RSI crosses below <c>OversoldThreshold</c> (default 30): entering oversold territory.</item>
///   <item>Confidence — scaled by depth below threshold: deeper oversold → higher confidence.</item>
///   <item>Exits — handled entirely by <c>ExitManager</c> (ATR-based stop-loss + profit target).</item>
/// </list>
/// </para>
/// Requires 16 bars minimum (14-period RSI needs 15 close prices + 1 ATR buffer).
/// Thread-safe via a private sync lock; all async work (filter checks, publish) runs outside the lock.
/// </summary>
public sealed class RsiMomentumStrategy(
    IEventBus eventBus,
    TradingOptions tradingOptions,
    ILogger<RsiMomentumStrategy> logger,
    ExecutionOptions? executionOptions = null) : IStrategy, IStrategyMetadata
{
    private readonly RsiMomentumOptions _options = tradingOptions.RsiMomentum;
    private readonly int _maxBarAgeMinutes = executionOptions?.MaxBarAgeMinutes ?? 3;

    // RequiredHistory: period bars for RSI changes + 1 prior close + 1 ATR buffer.
    private int RequiredBars => _options.Period + 2;

    // ── IStrategyMetadata ───────────────────────────────────────────────────
    public string StrategyName => "RSI_Momentum_14";
    public string Version => "1.0.0";
    public string? Description => $"RSI mean-reversion: BUY when RSI({_options.Period}) crosses below {_options.OversoldThreshold} (oversold).";

    // ── IStrategy ───────────────────────────────────────────────────────────
    public int RequiredHistory => RequiredBars;

    public bool IsReady =>
        _barHistories.Count > 0 &&
        _barHistories.Values.All(h => h.Count >= RequiredBars);

    // ── per-symbol state ────────────────────────────────────────────────────
    private readonly Dictionary<string, BarHistory> _barHistories = new();
    private readonly Dictionary<string, decimal> _previousRsi = new();
    private readonly HashSet<string> _readySymbols = new();
    private readonly object _syncLock = new();

    /// <summary>
    /// Processes <paramref name="bar"/>, updates indicators, and emits a BUY signal
    /// when RSI crosses from above into oversold territory.
    /// </summary>
    public async ValueTask OnBarAsync(BarEvent bar, CancellationToken ct = default)
    {
        SignalEvent? localSignal = null;

        lock (_syncLock)
        {
            if (!_barHistories.TryGetValue(bar.Symbol, out var history))
            {
                history = new BarHistory(RequiredBars + 10);
                _barHistories[bar.Symbol] = history;
            }

            history.AddBar(bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

            if (history.Count < RequiredBars)
            {
                logger.LogDebug("RSI strategy not ready for {Symbol}: {Count}/{Required} bars",
                    bar.Symbol, history.Count, RequiredBars);
                return;
            }

            if (_readySymbols.Add(bar.Symbol))
                logger.LogInformation("RSI strategy ready for {Symbol}: {Required} bars accumulated",
                    bar.Symbol, RequiredBars);

            var rsi = history.CalculateRsi(_options.Period);
            var atr = history.CalculateAtr(_options.Period);

            if (atr <= 0)
            {
                logger.LogWarning("Invalid ATR for {Symbol}: {Atr}", bar.Symbol, atr);
                atr = 0m;
            }

            logger.LogDebug(
                "RSI: {Symbol} | RSI={Rsi:F2} | ATR={Atr:F4} | Threshold={Threshold}",
                bar.Symbol, rsi, atr, _options.OversoldThreshold);

            // Staleness gate: update indicators but suppress signals for old bars.
            _previousRsi.TryGetValue(bar.Symbol, out var prevRsi);

            if (_maxBarAgeMinutes > 0)
            {
                var ageMinutes = (DateTimeOffset.UtcNow - bar.Timestamp).TotalMinutes;
                if (ageMinutes > _maxBarAgeMinutes)
                {
                    logger.LogDebug(
                        "RSI {Symbol} suppressed: age {Age:F1}min > {Max}min — indicators updated, no signal",
                        bar.Symbol, ageMinutes, _maxBarAgeMinutes);
                    _previousRsi[bar.Symbol] = rsi;
                    return;
                }
            }

            // BUY signal: RSI crosses INTO oversold territory.
            // prevRsi = 0 on the very first bar — treat as "above threshold" so we don't
            // emit a spurious signal on warmup; the real first crossover comes on the next bar.
            var enteringOversold = prevRsi >= _options.OversoldThreshold
                && rsi < _options.OversoldThreshold;

            _previousRsi[bar.Symbol] = rsi;

            if (!enteringOversold)
                return;

            var confidence = CalculateConfidence(rsi);

            logger.LogInformation(
                "RSI signal: {Symbol} BUY | RSI={Rsi:F2} (crossed below {Threshold}) | Confidence={Conf:F2}",
                bar.Symbol, rsi, _options.OversoldThreshold, confidence);

            var metadata = new SignalMetadata(
                SmaPeriod: (_options.Period, 0),        // Fast=RSI period, Slow unused
                FastSma: rsi,                            // Carries the RSI value
                MediumSma: _options.OversoldThreshold,  // Carries the trigger threshold
                SlowSma: 0m,
                Atr: atr > 0 ? atr : null,
                Confidence: confidence,
                Regime: "OVERSOLD",
                RegimeStrength: confidence,
                CurrentPrice: bar.Close,
                AtrValue: atr,
                RegimeType: "OVERSOLD",
                BarsInRegime: 0);

            localSignal = new SignalEvent(
                Symbol: bar.Symbol,
                Side: "BUY",
                Timeframe: bar.Timeframe,
                SignalTimestamp: bar.Timestamp,
                Metadata: metadata,
                StrategyName: StrategyName);
        }

        if (localSignal is not null)
            await eventBus.PublishAsync(localSignal, ct);
    }

    // ── private ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Confidence scales with depth below the oversold threshold.
    /// RSI at threshold → 0.50; RSI 10 points below threshold → 0.95 (clamped).
    /// </summary>
    private decimal CalculateConfidence(decimal rsi)
    {
        var depth = _options.OversoldThreshold - rsi; // positive when oversold
        var raw = 0.5m + depth / 10m;
        return Math.Clamp(raw, 0.30m, 0.95m);
    }
}
