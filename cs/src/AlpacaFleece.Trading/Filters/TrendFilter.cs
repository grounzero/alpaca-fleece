namespace AlpacaFleece.Trading.Filters;

/// <summary>
/// Filters signals based on daily trend direction: price must be above (BUY) or below (SELL)
/// a daily SMA. Caches daily bars per symbol with a 1-hour TTL to avoid repeated API calls.
/// </summary>
public sealed class TrendFilter(
    IMarketDataClient marketDataClient,
    TradingOptions options,
    ILogger<TrendFilter> logger)
{
    private readonly record struct CacheEntry(IReadOnlyList<Quote> Bars, DateTimeOffset CachedAt);

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Returns true if the signal aligns with the daily trend, or if the filter is disabled.
    /// Returns true (pass) on insufficient history — safe fallback, never blocks on missing data.
    /// </summary>
    public async ValueTask<bool> CheckAsync(string symbol, string side, CancellationToken ct = default)
    {
        if (!options.SignalFilters.EnableDailyTrendFilter)
            return true;

        var bars = await GetDailyBarsAsync(symbol, ct);
        var period = options.SignalFilters.DailySmaPeriod;

        if (bars.Count < period + 1)
        {
            logger.LogWarning(
                "TrendFilter: insufficient daily history for {Symbol} ({Count} bars, need {Need}) — passing signal",
                symbol, bars.Count, period + 1);
            return true;
        }

        var dailySma = CalculateSma(bars, period);
        var lastClose = bars[^1].Close;
        var passes = side == "BUY" ? lastClose > dailySma : lastClose < dailySma;

        if (!passes)
            logger.LogDebug(
                "TrendFilter: blocked {Symbol} {Side} — close={Close:F2} vs SMA({Period})={Sma:F2}",
                symbol, side, lastClose, period, dailySma);

        return passes;
    }

    private async ValueTask<IReadOnlyList<Quote>> GetDailyBarsAsync(string symbol, CancellationToken ct)
    {
        if (_cache.TryGetValue(symbol, out var entry) &&
            DateTimeOffset.UtcNow - entry.CachedAt < CacheTtl)
            return entry.Bars;

        var limit = options.SignalFilters.DailySmaPeriod + 5;
        var bars = await marketDataClient.GetBarsAsync(symbol, "1Day", limit, ct);
        _cache[symbol] = new CacheEntry(bars, DateTimeOffset.UtcNow);
        return bars;
    }

    private static decimal CalculateSma(IReadOnlyList<Quote> bars, int period)
    {
        var sum = 0m;
        for (var i = bars.Count - period; i < bars.Count; i++)
            sum += bars[i].Close;
        return sum / period;
    }
}

/// <summary>
/// Filters signals based on volume relative to a rolling average.
/// Current bar volume must be ≥ average × VolumeMultiplier to pass.
/// </summary>
public sealed class VolumeFilter(
    TradingOptions options,
    ILogger<VolumeFilter> logger)
{
    /// <summary>
    /// Returns true if current volume exceeds the rolling-average threshold, or if filter is disabled.
    /// Returns true (pass) on insufficient history — safe fallback, never blocks on missing data.
    /// </summary>
    public bool Check(IReadOnlyList<(decimal Open, decimal High, decimal Low, decimal Close, long Volume)> bars)
    {
        if (!options.SignalFilters.EnableVolumeFilter)
            return true;

        var lookback = options.SignalFilters.VolumeLookbackPeriod;
        if (bars.Count < lookback)
            return true;

        var currentVolume = bars[^1].Volume;

        var sum = 0L;
        for (var i = bars.Count - lookback; i < bars.Count; i++)
            sum += bars[i].Volume;

        var avg = (decimal)sum / lookback;
        var threshold = avg * options.SignalFilters.VolumeMultiplier;
        var passes = currentVolume >= threshold;

        if (!passes)
            logger.LogDebug(
                "VolumeFilter: blocked — volume={Volume} vs threshold={Threshold:F0} (avg={Avg:F0} × {Mult})",
                currentVolume, threshold, avg, options.SignalFilters.VolumeMultiplier);

        return passes;
    }
}
