namespace AlpacaFleece.Trading.Strategy;

/// <summary>
/// RSI mean-reversion strategy: emits BUY and SELL signals on RSI threshold crossovers.
/// <para>
/// This strategy is the natural complement to <see cref="SmaCrossoverStrategy"/>:
/// where the SMA crossover targets trending markets, RSI Momentum targets ranging /
/// mean-reverting markets by trading oversold bounces and overbought reversals.
/// </para>
/// <para>
/// Signal logic:
/// <list type="bullet">
///   <item>BUY — RSI crosses below <c>OversoldThreshold</c> (default 30): entering oversold territory.</item>
///   <item>SELL — RSI crosses above <c>OverboughtThreshold</c> (default 70): entering overbought territory.</item>
///   <item>Confidence — scaled by depth beyond threshold: deeper extreme → higher confidence.</item>
///   <item>Only one signal per bar (BUY and SELL are mutually exclusive by RSI arithmetic).</item>
/// </list>
/// </para>
/// Requires 16 bars minimum (14-period RSI needs 15 close prices + 1 ATR buffer).
/// Thread-safe via a private sync lock; all async work (publish) runs outside the lock.
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
    public string? Description => $"RSI mean-reversion: BUY when RSI({_options.Period}) crosses below {_options.OversoldThreshold} (oversold); SELL when RSI crosses above {_options.OverboughtThreshold} (overbought).";

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
    /// Processes <paramref name="bar"/>, updates indicators, and emits a BUY signal when RSI
    /// crosses into oversold territory or a SELL signal when RSI crosses into overbought territory.
    /// At most one signal is emitted per bar (BUY and SELL are mutually exclusive by RSI arithmetic).
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
                "RSI: {Symbol} | RSI={Rsi:F2} | ATR={Atr:F4} | Oversold={Oversold} | Overbought={Overbought}",
                bar.Symbol, rsi, atr, _options.OversoldThreshold, _options.OverboughtThreshold);

            // Staleness gate: update indicators but suppress signals for old bars.
            // hasPreviousRsi guards the first ready bar: prevRsi defaults to 0 (TryGetValue),
            // which would incorrectly satisfy prevRsi ≤ OverboughtThreshold on rising starts.
            var hasPreviousRsi = _previousRsi.TryGetValue(bar.Symbol, out var prevRsi);

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

            // BUY: RSI crosses INTO oversold territory (from above).
            // SELL: RSI crosses INTO overbought territory (from below).
            // hasPreviousRsi: both checks are skipped on the first ready bar so we never emit
            // a spurious signal when prevRsi happens to satisfy the crossover condition by default (0).
            var enteringOversold   = hasPreviousRsi && prevRsi >= _options.OversoldThreshold  && rsi < _options.OversoldThreshold;
            var enteringOverbought = hasPreviousRsi && prevRsi <= _options.OverboughtThreshold && rsi > _options.OverboughtThreshold;

            _previousRsi[bar.Symbol] = rsi;

            if (enteringOversold)
            {
                var confidence = CalculateOversoldConfidence(rsi);

                logger.LogInformation(
                    "RSI signal: {Symbol} BUY | RSI={Rsi:F2} (crossed below {Threshold}) | Confidence={Conf:F2}",
                    bar.Symbol, rsi, _options.OversoldThreshold, confidence);

                localSignal = new SignalEvent(
                    Symbol: bar.Symbol,
                    Side: "BUY",
                    Timeframe: bar.Timeframe,
                    SignalTimestamp: bar.Timestamp,
                    Metadata: new SignalMetadata(
                        SmaPeriod: (_options.Period, 0),
                        FastSma: rsi,
                        MediumSma: _options.OversoldThreshold,
                        SlowSma: 0m,
                        Atr: atr > 0 ? atr : null,
                        Confidence: confidence,
                        Regime: "OVERSOLD",
                        RegimeStrength: confidence,
                        CurrentPrice: bar.Close,
                        AtrValue: atr,
                        RegimeType: "OVERSOLD",
                        BarsInRegime: 0),
                    StrategyName: StrategyName);
            }
            else if (enteringOverbought)
            {
                var confidence = CalculateOverboughtConfidence(rsi);

                logger.LogInformation(
                    "RSI signal: {Symbol} SELL | RSI={Rsi:F2} (crossed above {Threshold}) | Confidence={Conf:F2}",
                    bar.Symbol, rsi, _options.OverboughtThreshold, confidence);

                localSignal = new SignalEvent(
                    Symbol: bar.Symbol,
                    Side: "SELL",
                    Timeframe: bar.Timeframe,
                    SignalTimestamp: bar.Timestamp,
                    Metadata: new SignalMetadata(
                        SmaPeriod: (_options.Period, 0),
                        FastSma: rsi,
                        MediumSma: _options.OverboughtThreshold,
                        SlowSma: 0m,
                        Atr: atr > 0 ? atr : null,
                        Confidence: confidence,
                        Regime: "OVERBOUGHT",
                        RegimeStrength: confidence,
                        CurrentPrice: bar.Close,
                        AtrValue: atr,
                        RegimeType: "OVERBOUGHT",
                        BarsInRegime: 0),
                    StrategyName: StrategyName);
            }
        }

        if (localSignal is not null)
            await eventBus.PublishAsync(localSignal, ct);
    }

    // ── private ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Confidence scales with depth below the oversold threshold.
    /// RSI at threshold → 0.50; RSI 10 points below threshold → 0.95 (clamped).
    /// </summary>
    private decimal CalculateOversoldConfidence(decimal rsi)
    {
        var depth = _options.OversoldThreshold - rsi; // positive when oversold
        var raw = 0.5m + depth / 10m;
        return Math.Clamp(raw, 0.30m, 0.95m);
    }

    /// <summary>
    /// Confidence scales with depth above the overbought threshold.
    /// RSI at threshold → 0.50; RSI 10 points above threshold → 0.95 (clamped).
    /// </summary>
    private decimal CalculateOverboughtConfidence(decimal rsi)
    {
        var depth = rsi - _options.OverboughtThreshold; // positive when overbought
        var raw = 0.5m + depth / 10m;
        return Math.Clamp(raw, 0.30m, 0.95m);
    }
}
