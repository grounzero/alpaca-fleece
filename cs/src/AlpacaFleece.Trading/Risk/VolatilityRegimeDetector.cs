namespace AlpacaFleece.Trading.Risk;

/// <summary>
/// Volatility regime classification used for adaptive sizing and ATR distance controls.
/// </summary>
public enum VolatilityRegime
{
    /// <summary>
    /// Low realised volatility conditions.
    /// </summary>
    Low,
    /// <summary>
    /// Baseline realised volatility conditions.
    /// </summary>
    Normal,
    /// <summary>
    /// Elevated realised volatility conditions.
    /// </summary>
    High,
    /// <summary>
    /// Extreme realised volatility conditions.
    /// </summary>
    Extreme
}

/// <summary>
/// Snapshot of the effective volatility regime and applied multipliers for a symbol.
/// </summary>
/// <param name="Regime">Detected volatility regime.</param>
/// <param name="RealisedVolatility">Computed realised volatility from recent 1-minute returns.</param>
/// <param name="BarsInRegime">Count of consecutive confirmed observations in the current regime.</param>
/// <param name="PositionMultiplier">Regime multiplier applied to position size.</param>
/// <param name="StopMultiplier">Regime multiplier applied to ATR stop/target distances.</param>
public sealed record VolatilityRegimeResult(
    VolatilityRegime Regime,
    decimal RealisedVolatility,
    int BarsInRegime,
    decimal PositionMultiplier,
    decimal StopMultiplier);

/// <summary>
/// Detects per-symbol volatility regime from realised volatility of recent 1-minute returns.
/// Includes hysteresis and transition confirmation to avoid rapid flip-flopping.
/// </summary>
public sealed class VolatilityRegimeDetector(
    IMarketDataClient marketDataClient,
    TradingOptions options,
    ILogger<VolatilityRegimeDetector> logger)
{
    private readonly VolatilityRegimeOptions _cfg = options.VolatilityRegime;
    private readonly HashSet<string> _cryptoSymbols = options.Symbols.CryptoSymbols
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private readonly record struct EffectiveProfile(
        int LookbackBars,
        int TransitionConfirmationBars,
        decimal HysteresisBuffer,
        decimal LowMaxVolatility,
        decimal NormalMaxVolatility,
        decimal HighMaxVolatility,
        decimal LowPositionMultiplier,
        decimal NormalPositionMultiplier,
        decimal HighPositionMultiplier,
        decimal ExtremePositionMultiplier,
        decimal LowStopMultiplier,
        decimal NormalStopMultiplier,
        decimal HighStopMultiplier,
        decimal ExtremeStopMultiplier);

    private sealed class State
    {
        public VolatilityRegime CurrentRegime { get; set; } = VolatilityRegime.Normal;
        public int BarsInRegime { get; set; } = 0;
        public VolatilityRegime? PendingRegime { get; set; }
        public int PendingBars { get; set; }
        public DateTimeOffset? LastObservedBarTimestamp { get; set; }
    }

    private readonly Dictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _stateLock = new();

    /// <summary>
    /// Gets a value indicating whether volatility regime adaptation is enabled.
    /// </summary>
    public bool Enabled => _cfg.Enabled;

    /// <summary>
    /// Computes and returns the current volatility regime for a symbol.
    /// </summary>
    /// <param name="symbol">Broker symbol to evaluate.</param>
    /// <param name="ct">Cancellation token for the market-data request.</param>
    /// <returns>
    /// A <see cref="VolatilityRegimeResult"/> describing the detected regime and effective multipliers.
    /// Returns <see cref="VolatilityRegime.Normal"/> with neutral multipliers when disabled or on safe fallback paths.
    /// </returns>
    /// <remarks>
    /// Repeated calls for the same latest bar timestamp return a stable result without advancing
    /// transition counters, so regime duration semantics remain bar-driven rather than poll-driven.
    /// </remarks>
    public async ValueTask<VolatilityRegimeResult> GetRegimeAsync(
        string symbol,
        CancellationToken ct = default)
    {
        if (!_cfg.Enabled)
            return BuildResult(VolatilityRegime.Normal, 0m, barsInRegime: 0, ResolveProfile(symbol));

        try
        {
            var profile = ResolveProfile(symbol);
            var lookback = Math.Max(5, profile.LookbackBars);
            var bars = await marketDataClient.GetBarsAsync(symbol, "1m", lookback + 1, ct);
            if (bars.Count < 2)
                return BuildResult(VolatilityRegime.Normal, 0m, barsInRegime: 0, profile);

            var vol = CalculateRealisedVolatility(bars);
            var latestBarTimestamp = bars[^1].Timestamp;

            lock (_stateLock)
            {
                if (_states.TryGetValue(symbol, out var existingState) &&
                    existingState.LastObservedBarTimestamp == latestBarTimestamp)
                {
                    // Repeated poll for the same latest bar: return stable state without
                    // advancing BarsInRegime/PendingBars on call frequency.
                    return BuildResult(existingState.CurrentRegime, vol, existingState.BarsInRegime, profile);
                }
            }

            var classified = ClassifyFromVolatility(symbol, vol);

            lock (_stateLock)
            {
                if (_states.TryGetValue(symbol, out var updatedState))
                    updatedState.LastObservedBarTimestamp = latestBarTimestamp;
            }

            return classified;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Volatility regime fetch failed for {Symbol}; falling back to NORMAL",
                symbol);
            return BuildResult(VolatilityRegime.Normal, 0m, barsInRegime: 0, ResolveProfile(symbol));
        }
    }

    /// <summary>
    /// Classifies a provided realised-volatility sample. Public for deterministic testing.
    /// </summary>
    public VolatilityRegimeResult ClassifyFromVolatility(string symbol, decimal realisedVolatility)
    {
        if (!_cfg.Enabled)
            return BuildResult(VolatilityRegime.Normal, realisedVolatility, barsInRegime: 0, ResolveProfile(symbol));

        var profile = ResolveProfile(symbol);

        lock (_stateLock)
        {
            if (!_states.TryGetValue(symbol, out var state))
            {
                var initial = ClassifyRaw(realisedVolatility, profile);
                state = new State { CurrentRegime = initial, BarsInRegime = 1 };
                _states[symbol] = state;
                return BuildResult(initial, realisedVolatility, state.BarsInRegime, profile);
            }

            var raw = ClassifyWithHysteresis(realisedVolatility, state.CurrentRegime, profile);
            if (raw == state.CurrentRegime)
            {
                state.BarsInRegime++;
                state.PendingRegime = null;
                state.PendingBars = 0;
                return BuildResult(state.CurrentRegime, realisedVolatility, state.BarsInRegime, profile);
            }

            if (state.PendingRegime == raw)
                state.PendingBars++;
            else
            {
                state.PendingRegime = raw;
                state.PendingBars = 1;
            }

            if (state.PendingBars >= Math.Max(1, profile.TransitionConfirmationBars))
            {
                var previous = state.CurrentRegime;
                state.CurrentRegime = raw;
                state.BarsInRegime = 1;
                state.PendingRegime = null;
                state.PendingBars = 0;

                logger.LogInformation(
                    "Volatility regime transition for {Symbol}: {Previous} -> {Current} (vol={Vol:F6})",
                    symbol, previous, state.CurrentRegime, realisedVolatility);
                return BuildResult(state.CurrentRegime, realisedVolatility, state.BarsInRegime, profile);
            }

            state.BarsInRegime++;
            return BuildResult(state.CurrentRegime, realisedVolatility, state.BarsInRegime, profile);
        }
    }

    private VolatilityRegime ClassifyWithHysteresis(
        decimal vol,
        VolatilityRegime current,
        EffectiveProfile profile)
    {
        var baseRaw = ClassifyRaw(vol, profile);
        if (baseRaw == current)
            return current;

        var b = Math.Max(0m, profile.HysteresisBuffer);
        return current switch
        {
            VolatilityRegime.Low when vol <= profile.LowMaxVolatility + b => VolatilityRegime.Low,
            VolatilityRegime.Normal when vol >= profile.LowMaxVolatility - b && vol <= profile.NormalMaxVolatility + b
                => VolatilityRegime.Normal,
            VolatilityRegime.High when vol >= profile.NormalMaxVolatility - b && vol <= profile.HighMaxVolatility + b
                => VolatilityRegime.High,
            VolatilityRegime.Extreme when vol >= profile.HighMaxVolatility - b => VolatilityRegime.Extreme,
            _ => baseRaw
        };
    }

    private static VolatilityRegime ClassifyRaw(decimal vol, EffectiveProfile profile)
    {
        if (vol <= profile.LowMaxVolatility) return VolatilityRegime.Low;
        if (vol <= profile.NormalMaxVolatility) return VolatilityRegime.Normal;
        if (vol <= profile.HighMaxVolatility) return VolatilityRegime.High;
        return VolatilityRegime.Extreme;
    }

    private VolatilityRegimeResult BuildResult(
        VolatilityRegime regime,
        decimal realisedVolatility,
        int barsInRegime,
        EffectiveProfile profile)
    {
        var (positionMultiplier, stopMultiplier) = regime switch
        {
            VolatilityRegime.Low => (profile.LowPositionMultiplier, profile.LowStopMultiplier),
            VolatilityRegime.Normal => (profile.NormalPositionMultiplier, profile.NormalStopMultiplier),
            VolatilityRegime.High => (profile.HighPositionMultiplier, profile.HighStopMultiplier),
            _ => (profile.ExtremePositionMultiplier, profile.ExtremeStopMultiplier)
        };

        return new VolatilityRegimeResult(
            regime,
            realisedVolatility,
            Math.Max(0, barsInRegime),
            positionMultiplier,
            stopMultiplier);
    }

    private EffectiveProfile ResolveProfile(string symbol)
    {
        var overrides = _cryptoSymbols.Contains(symbol) ? _cfg.Crypto : _cfg.Equity;
        return new EffectiveProfile(
            LookbackBars: overrides?.LookbackBars ?? _cfg.LookbackBars,
            TransitionConfirmationBars: overrides?.TransitionConfirmationBars ?? _cfg.TransitionConfirmationBars,
            HysteresisBuffer: overrides?.HysteresisBuffer ?? _cfg.HysteresisBuffer,
            LowMaxVolatility: overrides?.LowMaxVolatility ?? _cfg.LowMaxVolatility,
            NormalMaxVolatility: overrides?.NormalMaxVolatility ?? _cfg.NormalMaxVolatility,
            HighMaxVolatility: overrides?.HighMaxVolatility ?? _cfg.HighMaxVolatility,
            LowPositionMultiplier: overrides?.LowPositionMultiplier ?? _cfg.LowPositionMultiplier,
            NormalPositionMultiplier: overrides?.NormalPositionMultiplier ?? _cfg.NormalPositionMultiplier,
            HighPositionMultiplier: overrides?.HighPositionMultiplier ?? _cfg.HighPositionMultiplier,
            ExtremePositionMultiplier: overrides?.ExtremePositionMultiplier ?? _cfg.ExtremePositionMultiplier,
            LowStopMultiplier: overrides?.LowStopMultiplier ?? _cfg.LowStopMultiplier,
            NormalStopMultiplier: overrides?.NormalStopMultiplier ?? _cfg.NormalStopMultiplier,
            HighStopMultiplier: overrides?.HighStopMultiplier ?? _cfg.HighStopMultiplier,
            ExtremeStopMultiplier: overrides?.ExtremeStopMultiplier ?? _cfg.ExtremeStopMultiplier);
    }

    private static decimal CalculateRealisedVolatility(IReadOnlyList<Quote> bars)
    {
        if (bars.Count < 2)
            return 0m;

        var returns = new List<decimal>(bars.Count - 1);
        for (var i = 1; i < bars.Count; i++)
        {
            var prev = bars[i - 1].Close;
            var cur = bars[i].Close;
            if (prev <= 0m || cur <= 0m)
                continue;

            returns.Add((cur / prev) - 1m);
        }

        if (returns.Count < 2)
            return 0m;

        var mean = returns.Average();
        var sumSq = 0m;
        foreach (var r in returns)
        {
            var d = r - mean;
            sumSq += d * d;
        }

        var variance = sumSq / (returns.Count - 1);
        return variance <= 0m
            ? 0m
            : (decimal)Math.Sqrt((double)variance);
    }
}
