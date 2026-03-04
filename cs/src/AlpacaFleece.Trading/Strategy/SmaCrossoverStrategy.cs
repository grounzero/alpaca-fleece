namespace AlpacaFleece.Trading.Strategy;

/// <summary>
/// Multi-timeframe SMA crossover strategy with full ATR wiring and regime detection.
/// Uses 3 SMA pairs: (5,15), (10,30), (20,50).
/// Emits signals based on crossovers with confidence scoring.
/// Requires 51 bars minimum (20 + 14 ATR + buffer).
/// </summary>
public sealed class SmaCrossoverStrategy(
    IEventBus eventBus,
    ILogger<SmaCrossoverStrategy> logger,
    TrendFilter? trendFilter = null,
    VolumeFilter? volumeFilter = null,
    ExecutionOptions? executionOptions = null) : IStrategy
{
    private readonly RegimeDetector _regimeDetector = new();
    private readonly Dictionary<string, BarHistory> _barHistories = new();
    private readonly object _syncLock = new();
    private readonly int _maxBarAgeMinutes = executionOptions?.MaxBarAgeMinutes ?? 3;
    // Tracks which symbols have already logged the "strategy ready" transition.
    private readonly HashSet<string> _readySymbols = new();

    // SMA periods: 3 pairs for multi-timeframe analysis
    private const int FastPeriod1 = 5;
    private const int SlowPeriod1 = 15;
    private const int FastPeriod2 = 10;
    private const int SlowPeriod2 = 30;
    private const int FastPeriod3 = 20;
    private const int SlowPeriod3 = 50;
    private const int AtrPeriod = 14;
    private const int RequiredBars = 51; // 50 (slowest SMA) + 1 for valid ATR

    // Previous SMA states for crossover detection
    private readonly Dictionary<string, (decimal, decimal)> _previousSmaPair1 = new();
    private readonly Dictionary<string, (decimal, decimal)> _previousSmaPair2 = new();
    private readonly Dictionary<string, (decimal, decimal)> _previousSmaPair3 = new();

    public int RequiredHistory => RequiredBars;
    public bool IsReady => _barHistories.Count > 0 && _barHistories.Values.All(h => h.Count >= RequiredBars);

    /// <summary>
    /// Processes incoming bar and emits signals if conditions met.
    /// Returns list of SignalEvents (typically 0-3 per bar).
    /// </summary>
    public async ValueTask OnBarAsync(BarEvent bar, CancellationToken ct = default)
    {
        List<SignalEvent> localSignals = [];
        IReadOnlyList<(decimal Open, decimal High, decimal Low, decimal Close, long Volume)>? localSnapshot = null;

        lock (_syncLock)
        {
            // Get history for symbol if needed
            if (!_barHistories.TryGetValue(bar.Symbol, out var history))
            {
                history = new BarHistory(RequiredBars + 10); // Small buffer
                _barHistories[bar.Symbol] = history;
            }

            // Add bar to history
            history.AddBar(bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

            // Not ready yet
            if (history.Count < RequiredBars)
            {
                logger.LogDebug("Strategy not ready for {Symbol}: {Available}/{Required} bars",
                    bar.Symbol, history.Count, RequiredBars);
                return;
            }

            // Log once per symbol when it first crosses the warmup threshold
            if (_readySymbols.Add(bar.Symbol))
                logger.LogInformation("Strategy ready for {Symbol}: {Required} bars accumulated",
                    bar.Symbol, RequiredBars);

            // Staleness gate: bars older than MaxBarAgeMinutes update indicators (warm-up)
            // but produce no signals. Prevents stale replay bars from triggering orders on restart.
            if (_maxBarAgeMinutes > 0)
            {
                var ageMinutes = (DateTimeOffset.UtcNow - bar.Timestamp).TotalMinutes;
                if (ageMinutes > _maxBarAgeMinutes)
                {
                    logger.LogDebug(
                        "Bar {Symbol} suppressed: age {Age:F1}min > threshold {Max}min — indicators updated, no signal",
                        bar.Symbol, ageMinutes, _maxBarAgeMinutes);
                    return;
                }
            }

            // Calculate SMAs and ATR
            var fast1 = history.CalculateSma(FastPeriod1);
            var slow1 = history.CalculateSma(SlowPeriod1);
            var fast2 = history.CalculateSma(FastPeriod2);
            var slow2 = history.CalculateSma(SlowPeriod2);
            var fast3 = history.CalculateSma(FastPeriod3);
            var slow3 = history.CalculateSma(SlowPeriod3);
            var atr = history.CalculateAtr(AtrPeriod);

            // Validate ATR before using it in signals; if invalid, log and set to 0 to avoid misleading values in metadata
            if (atr <= 0)
            {
                logger.LogWarning("Invalid ATR for {symbol}: {atr}", bar.Symbol, atr);
                atr = 0;
            }

            // Detect regime
            var regime = _regimeDetector.DetectRegime(fast3, fast2, fast1);

            logger.LogDebug(
                "Bar: {symbol} | P1:({fast1:F2},{slow1:F2}) P2:({fast2:F2},{slow2:F2}) P3:({fast3:F2},{slow3:F2}) | Regime={regime} | ATR={atr:F2}",
                bar.Symbol, fast1, slow1, fast2, slow2, fast3, slow3, regime.RegimeType, atr);

            // Generate signals based on crossovers
            var signals = new List<SignalEvent>();

            // Check Pair 1 (5,15) crossover with confidence scoring
            CheckCrossoverAndEmit(
                signals,
                bar,
                fast1,
                slow1,
                (FastPeriod1, SlowPeriod1),
                fast2,
                slow2,
                fast3,
                slow3,
                atr,
                regime,
                _previousSmaPair1,
                "pair1");

            // Check Pair 2 (10,30) crossover
            CheckCrossoverAndEmit(
                signals,
                bar,
                fast2,
                slow2,
                (FastPeriod2, SlowPeriod2),
                fast1,
                slow1,
                fast3,
                slow3,
                atr,
                regime,
                _previousSmaPair2,
                "pair2");

            // Check Pair 3 (20,50) crossover
            CheckCrossoverAndEmit(
                signals,
                bar,
                fast3,
                slow3,
                (FastPeriod3, SlowPeriod3),
                fast1,
                slow1,
                fast2,
                slow2,
                atr,
                regime,
                _previousSmaPair3,
                "pair3");

            // Update previous states
            _previousSmaPair1[bar.Symbol] = (fast1, slow1);
            _previousSmaPair2[bar.Symbol] = (fast2, slow2);
            _previousSmaPair3[bar.Symbol] = (fast3, slow3);

            // Capture signals and snapshot before releasing the lock.
            // Locals are iterated outside the lock so async filter/publish calls are safe.
            localSignals = signals;
            if (localSignals.Count > 0)
                localSnapshot = history.GetBars().ToArray();
        }

        // Apply filters and publish signals outside the lock (async calls cannot be inside lock)
        foreach (var signal in localSignals)
        {
            if (volumeFilter != null && localSnapshot != null
                && !volumeFilter.Check(localSnapshot))
            {
                logger.LogDebug(
                    "Volume filter blocked {Symbol} {Side}", signal.Symbol, signal.Side);
                continue;
            }

            if (trendFilter != null && !await trendFilter.CheckAsync(signal.Symbol, signal.Side, ct))
            {
                logger.LogDebug(
                    "Trend filter blocked {Symbol} {Side}", signal.Symbol, signal.Side);
                continue;
            }

            await eventBus.PublishAsync(signal, ct);
        }
    }

    /// <summary>
    /// Checks for SMA crossover and emits signal if detected.
    /// Includes confidence scoring based on regime alignment.
    /// </summary>
    private void CheckCrossoverAndEmit(
        List<SignalEvent> signals,
        BarEvent bar,
        decimal fastSma,
        decimal slowSma,
        (int Fast, int Slow) period,
        decimal otherFast,
        decimal otherSlow,
        decimal slowestSma,
        decimal slowestFast,
        decimal atr,
        RegimeScore regime,
        Dictionary<string, (decimal, decimal)> previousState,
        string pairName)
    {
        // Get previous state
        previousState.TryGetValue(bar.Symbol, out var prevPair);
        var prevFast = prevPair.Item1;
        var prevSlow = prevPair.Item2;

        // Detect crossover: fast crosses above slow (BUY) or below (SELL)
        var isCrossoverUp = prevFast <= prevSlow && fastSma > slowSma;
        var isCrossoverDown = prevFast >= prevSlow && fastSma < slowSma;

        if (!isCrossoverUp && !isCrossoverDown)
            return;

        // Calculate confidence score based on regime alignment
        var confidence = CalculateConfidence(
            fastSma,
            slowSma,
            otherFast,
            otherSlow,
            slowestSma,
            slowestFast,
            regime,
            isCrossoverUp);

        var side = isCrossoverUp ? "BUY" : "SELL";

        logger.LogInformation(
            "Signal: {symbol} {side} on {pair} | Confidence={conf:F2} | Regime={regime}",
            bar.Symbol, side, pairName, confidence, regime.RegimeType);

        var metadata = new SignalMetadata(
            SmaPeriod: period,
            FastSma: fastSma,
            MediumSma: otherFast,
            SlowSma: slowSma,
            Atr: atr > 0 ? atr : null,
            Confidence: confidence,
            Regime: regime.RegimeType,
            RegimeStrength: regime.Strength,
            CurrentPrice: bar.Close,
            AtrValue: atr,
            RegimeType: regime.RegimeType,
            BarsInRegime: regime.BarsInRegime);

        var signal = new SignalEvent(
            Symbol: bar.Symbol,
            Side: side,
            Timeframe: bar.Timeframe,
            SignalTimestamp: bar.Timestamp,
            Metadata: metadata);

        signals.Add(signal);
    }

    /// <summary>
    /// Calculates confidence score based on regime alignment.
    /// Trending + slow SMA = 0.9
    /// Trending + fast SMA = 0.5
    /// Ranging + fast SMA = 0.2
    /// </summary>
    private decimal CalculateConfidence(
        decimal fastSma,
        decimal slowSma,
        decimal otherFast,
        decimal otherSlow,
        decimal slowestSma,
        decimal slowestFast,
        RegimeScore regime,
        bool isBuy)
    {
        // Check if slow SMA aligns with signal
        var slowAligned = isBuy ? slowestFast > slowestSma : slowestFast < slowestSma;

        // Base confidence on regime
        var confidence = regime.RegimeType switch
        {
            "TRENDING_UP" when isBuy => 0.8m,
            "TRENDING_DOWN" when !isBuy => 0.8m,
            "TRENDING_UP" or "TRENDING_DOWN" => 0.5m,
            _ => 0.2m // Ranging
        };

        // Boost if slow SMA aligned
        if (slowAligned)
            confidence = Math.Min(1m, confidence + 0.1m);

        // Apply regime strength modifier
        confidence = Math.Min(1m, confidence * regime.Strength);

        return Math.Max(0.1m, Math.Min(1m, confidence));
    }
}
