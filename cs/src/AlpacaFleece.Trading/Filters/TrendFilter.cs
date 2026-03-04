namespace AlpacaFleece.Trading.Filters;

/// <summary>
/// Filters signals based on daily trend direction.
/// Compares the most recent daily bar's close to a simple moving average of daily closes.
/// A BUY signal passes when close &gt; SMA; a SELL signal passes when close &lt; SMA.
/// Uses the latest available daily close to reflect the daily trend direction, and may include
/// the current trading day's bar depending on the data provider's behavior.
/// Caches daily bars per symbol with a 1-hour TTL to avoid repeated API calls.
/// </summary>
public sealed class TrendFilter(
    IMarketDataClient marketDataClient,
    TradingOptions options,
    ILogger<TrendFilter> logger)
{
    private readonly record struct CacheEntry(IReadOnlyList<Quote> Bars, DateTimeOffset CachedAt);

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Returns true if the signal aligns with the daily trend, or if the filter is disabled.
    /// Returns true (pass) on insufficient history — safe fallback, never blocks on missing data.
    /// </summary>
    public async ValueTask<bool> CheckAsync(string symbol, string side, CancellationToken ct = default)
    {
        if (!options.SignalFilters.EnableDailyTrendFilter)
            return true;

        IReadOnlyList<Quote> bars;
        try
        {
            bars = await GetDailyBarsAsync(symbol, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "TrendFilter: failed to fetch daily bars for {Symbol} — passing signal",
                symbol);
            return true;
        }
        // Cap period to the maximum bars GetDailyBarsAsync can ever return (limit - 5 = 995).
        // GetDailyBarsAsync clamps its fetch to 1 000; if DailySmaPeriod > 995 the bars.Count
        // check below would always pass-through rather than filtering.
        var period = Math.Min(Math.Max(2, options.SignalFilters.DailySmaPeriod), 995);
        if (options.SignalFilters.DailySmaPeriod > period)
            logger.LogWarning(
                "TrendFilter: DailySmaPeriod {Configured} exceeds the API fetch cap; effective period clamped to {Effective}",
                options.SignalFilters.DailySmaPeriod, period);

        if (bars.Count < period)
        {
            logger.LogDebug(
                "TrendFilter: insufficient daily history for {Symbol} ({Count} bars, need {Need}) — passing signal",
                symbol, bars.Count, period);
            return true;
        }

        var dailySma = CalculateSma(bars, period);
        var lastClose = bars[^1].Close;

        bool passes;
        if (string.Equals(side, "BUY", StringComparison.OrdinalIgnoreCase))
            passes = lastClose > dailySma;
        else if (string.Equals(side, "SELL", StringComparison.OrdinalIgnoreCase))
            passes = lastClose < dailySma;
        else
        {
            logger.LogWarning(
                "TrendFilter: unknown side '{Side}' for {Symbol} — passing signal",
                side, symbol);
            return true;
        }

        if (!passes)
            logger.LogDebug(
                "TrendFilter: blocked {Symbol} {Side} — close={Close:F2} vs SMA({Period})={Sma:F2}",
                symbol, side, lastClose, period, dailySma);

        return passes;
    }

    private async ValueTask<IReadOnlyList<Quote>> GetDailyBarsAsync(string symbol, CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(symbol, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.CachedAt < CacheTtl)
                    return entry.Bars;
                // Entry expired — evict now so the dictionary doesn't grow unbounded
                _cache.Remove(symbol);
            }
        }

        var limit = Math.Min(Math.Max(2, options.SignalFilters.DailySmaPeriod) + 5, 1_000);
        var bars = await marketDataClient.GetBarsAsync(symbol, "1Day", limit, ct);

        lock (_cacheLock)
        {
            _cache[symbol] = new CacheEntry(bars, DateTimeOffset.UtcNow);
        }
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

