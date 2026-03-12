namespace AlpacaFleece.Trading.Strategies;

/// <summary>
/// Maintains a per-symbol SMA-based regime estimate that the <see cref="StrategyOrchestrator"/>
/// uses to select which strategies are active on each bar when running in Regime mode.
/// <para>
/// The regime is derived from the alignment of three simple moving averages:
/// <list type="bullet">
///   <item><description><b>TRENDING_UP</b> — fast &gt; medium &gt; slow (bullish alignment)</description></item>
///   <item><description><b>TRENDING_DOWN</b> — fast &lt; medium &lt; slow (bearish alignment)</description></item>
///   <item><description><b>RANGING</b> — SMAs are not aligned</description></item>
/// </list>
/// Before enough bars have accumulated the returned regime is <c>"DEFAULT"</c>, which maps to the
/// default strategy via <see cref="StrategySelectionOptions.RegimeMappings"/>.
/// </para>
/// <para>
/// Uses the same SMA periods as <see cref="SmaCrossoverStrategy"/>'s internal regime detector
/// (fast=5, medium=10, slow=20) to keep routing consistent with the strategy's own view of the regime.
/// Requires 21 bars per symbol before a regime is first determined.
/// </para>
/// Thread-safe via a private <see cref="Lock"/>.
/// </summary>
public sealed class RegimeRouter(ILogger<RegimeRouter> logger)
{
    private const int FastPeriod   = 5;
    private const int MediumPeriod = 10;
    private const int SlowPeriod   = 20;
    private const int RequiredBars = SlowPeriod + 1; // 21
    private const int BufferSize   = RequiredBars + 10;

    // RegimeDetector is in AlpacaFleece.Trading.Strategy — accessible via GlobalUsings.
    private readonly RegimeDetector _detector = new();
    private readonly Dictionary<string, BarHistory> _histories =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _regimes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>
    /// Feeds <paramref name="bar"/> into the router's history and updates the regime estimate
    /// for <paramref name="bar"/>.<c>Symbol</c>.
    /// Must be called once per bar, before <see cref="GetRegime"/>, by the orchestrator.
    /// </summary>
    public void Update(BarEvent bar)
    {
        lock (_lock)
        {
            if (!_histories.TryGetValue(bar.Symbol, out var history))
            {
                history = new BarHistory(BufferSize);
                _histories[bar.Symbol] = history;
            }

            history.AddBar(bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

            if (history.Count < RequiredBars)
                return;

            var fast   = history.CalculateSma(FastPeriod);
            var medium = history.CalculateSma(MediumPeriod);
            var slow   = history.CalculateSma(SlowPeriod);

            var score = _detector.DetectRegime(bar.Symbol, fast, medium, slow);
            _regimes[bar.Symbol] = score.RegimeType;

            logger.LogDebug(
                "RegimeRouter: {Symbol} → {Regime} (strength={Strength:F2}, bars={Bars})",
                bar.Symbol, score.RegimeType, score.Strength, score.BarsInRegime);
        }
    }

    /// <summary>
    /// Returns the current regime for <paramref name="symbol"/>.
    /// Returns <c>"DEFAULT"</c> when fewer than <see cref="RequiredBars"/> bars have been fed
    /// for this symbol (pre-warmup period).
    /// </summary>
    public string GetRegime(string symbol)
    {
        lock (_lock)
            return _regimes.TryGetValue(symbol, out var regime) ? regime : "DEFAULT";
    }
}
