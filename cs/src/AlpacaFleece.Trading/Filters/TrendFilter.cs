namespace AlpacaFleece.Trading.Filters;

/// <summary>
/// Filters signals based on multiple trend indicators: daily SMA, ADX, slope, weekly SMA.
/// All checks are optional and can be disabled via configuration.
/// When a check is disabled, the signal passes that check.
/// Caches daily/weekly bars per symbol with a 1-hour TTL to avoid repeated API calls.
/// </summary>
public sealed class TrendFilter(
    IMarketDataClient marketDataClient,
    TradingOptions options,
    ILogger<TrendFilter> logger)
{
    private readonly record struct CacheEntry(IReadOnlyList<Quote> Bars, DateTimeOffset CachedAt);

    private readonly Dictionary<string, CacheEntry> _dailyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CacheEntry> _weeklyCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    /// <summary>
    /// Returns true if the signal passes all enabled trend filters.
    /// Returns true (pass) on insufficient history — safe fallback, never blocks on missing data.
    /// </summary>
    public async ValueTask<bool> CheckAsync(string symbol, string side, CancellationToken ct = default)
    {
        var filters = options.SignalFilters;
        var symbolOverride = filters.SymbolOverrides.GetValueOrDefault(symbol);

        // Check 1: Daily SMA trend
        if (GetBool(filters.EnableDailyTrendFilter, symbolOverride?.EnableDailyTrendFilter) ?? true)
        {
            if (!await CheckDailyTrendAsync(symbol, side, ct))
            {
                logger.LogDebug("TrendFilter: daily SMA blocked {Symbol} {Side}", symbol, side);
                return false;
            }
        }

        // Check 2: ADX (trend strength)
        if (GetBool(filters.EnableAdxFilter, symbolOverride?.EnableAdxFilter) ?? false)
        {
            var minAdx = symbolOverride?.MinAdx ?? filters.MinAdx;
            if (!await CheckAdxAsync(symbol, minAdx, ct))
            {
                logger.LogDebug("TrendFilter: ADX blocked {Symbol} (below {MinAdx})", symbol, minAdx);
                return false;
            }
        }

        // Check 3: Slope (momentum)
        if (GetBool(filters.EnableSlopeFilter, symbolOverride?.EnableSlopeFilter) ?? false)
        {
            var minSlope = symbolOverride?.MinSlopePercent ?? filters.MinSlopePercent;
            if (!await CheckSlopeAsync(symbol, minSlope, ct))
            {
                logger.LogDebug("TrendFilter: slope blocked {Symbol} (below {MinSlope}%)", symbol, minSlope * 100);
                return false;
            }
        }

        // Check 4: Weekly SMA confirmation
        if (GetBool(filters.EnableWeeklyConfirmation, symbolOverride?.EnableWeeklyConfirmation) ?? false)
        {
            if (!await CheckWeeklyConfirmationAsync(symbol, side, ct))
            {
                logger.LogDebug("TrendFilter: weekly SMA blocked {Symbol} {Side}", symbol, side);
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Daily SMA check: BUY requires close &gt; SMA, SELL requires close &lt; SMA.
    /// </summary>
    private async ValueTask<bool> CheckDailyTrendAsync(string symbol, string side, CancellationToken ct)
    {
        try
        {
            var bars = await GetDailyBarsAsync(symbol, ct);
            var period = Math.Min(Math.Max(2, options.SignalFilters.DailySmaPeriod), 995);

            if (bars.Count < period)
                return true; // Pass-through on insufficient history

            var sma = CalculateSma(bars, period);
            var lastClose = bars[^1].Close;

            return side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? lastClose > sma : lastClose < sma;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TrendFilter: daily SMA check failed for {Symbol}, passing", symbol);
            return true;
        }
    }

    /// <summary>
    /// ADX check: blocks if ADX is below MinAdx (indicating ranging market).
    /// Uses simplified ADX calculation (DI+/DI-/DX smoothing).
    /// </summary>
    private async ValueTask<bool> CheckAdxAsync(string symbol, decimal minAdx, CancellationToken ct)
    {
        try
        {
            var bars = await GetDailyBarsAsync(symbol, ct);
            var period = options.SignalFilters.AdxPeriod;

            // Need at least period + 1 bars for ADX calculation
            if (bars.Count < period + 1)
                return true; // Pass-through on insufficient history

            var adx = CalculateAdx(bars, period);
            return adx >= minAdx;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TrendFilter: ADX check failed for {Symbol}, passing", symbol);
            return true;
        }
    }

    /// <summary>
    /// Slope check: blocks if SMA momentum is too shallow (low momentum).
    /// Compares SMA[today] vs SMA[N periods ago].
    /// </summary>
    private async ValueTask<bool> CheckSlopeAsync(string symbol, decimal minSlope, CancellationToken ct)
    {
        try
        {
            var bars = await GetDailyBarsAsync(symbol, ct);
            var period = options.SignalFilters.SlopePeriod;
            var smaLength = 5; // Use 5-bar SMA for slope calculation

            if (bars.Count < period + smaLength)
                return true; // Pass-through on insufficient history

            // Calculate two SMA points: current and N periods ago
            var currentSma = CalculateSma(bars.TakeLast(smaLength).ToList(), smaLength);
            var previousSmaIndex = bars.Count - smaLength - period;
            var previousBars = bars.Skip(previousSmaIndex).Take(smaLength).ToList();

            if (previousBars.Count < smaLength)
                return true;

            var previousSma = CalculateSma(previousBars, smaLength);

            // Calculate slope as percentage change per bar
            if (previousSma == 0)
                return true;

            var slope = Math.Abs((currentSma - previousSma) / previousSma / period);
            return slope >= minSlope;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TrendFilter: slope check failed for {Symbol}, passing", symbol);
            return true;
        }
    }

    /// <summary>
    /// Weekly SMA confirmation: BUY requires price &gt; weekly SMA, SELL requires price &lt; weekly SMA.
    /// </summary>
    private async ValueTask<bool> CheckWeeklyConfirmationAsync(string symbol, string side, CancellationToken ct)
    {
        try
        {
            var bars = await GetWeeklyBarsAsync(symbol, ct);
            var period = options.SignalFilters.WeeklySmaPeriod;

            if (bars.Count < period)
                return true; // Pass-through on insufficient history

            var sma = CalculateSma(bars, period);
            var lastClose = bars[^1].Close;

            return side.Equals("BUY", StringComparison.OrdinalIgnoreCase) ? lastClose > sma : lastClose < sma;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "TrendFilter: weekly SMA check failed for {Symbol}, passing", symbol);
            return true;
        }
    }

    /// <summary>
    /// Simplified ADX calculation (DI+, DI-, DX, smoothing).
    /// Returns the average DX over the last `period` bars.
    /// </summary>
    private static decimal CalculateAdx(IReadOnlyList<Quote> bars, int period)
    {
        if (bars.Count < period + 1)
            return 0m;

        var dxValues = new List<decimal>();

        for (int i = 1; i < bars.Count; i++)
        {
            var curr = bars[i];
            var prev = bars[i - 1];

            var trueRange = Math.Max(
                curr.High - curr.Low,
                Math.Max(
                    Math.Abs(curr.High - prev.Close),
                    Math.Abs(curr.Low - prev.Close)));

            if (trueRange == 0)
                continue;

            var upMove = curr.High - prev.High;
            var downMove = prev.Low - curr.Low;

            var diPlus = (upMove > downMove && upMove > 0) ? upMove / trueRange : 0m;
            var diMinus = (downMove > upMove && downMove > 0) ? downMove / trueRange : 0m;

            var sum = diPlus + diMinus;
            if (sum > 0)
            {
                var dx = Math.Abs(diPlus - diMinus) / sum * 100;
                dxValues.Add(dx);
            }
        }

        if (dxValues.Count == 0)
            return 0m;

        // Return average of the last `period` DX values (simplified ADX)
        var count = Math.Min(period, dxValues.Count);
        return dxValues.TakeLast(count).Average();
    }

    private async ValueTask<IReadOnlyList<Quote>> GetDailyBarsAsync(string symbol, CancellationToken ct)
    {
        return await GetBarsFromCacheAsync(symbol, "1Day", _dailyCache, 1_000, ct);
    }

    private async ValueTask<IReadOnlyList<Quote>> GetWeeklyBarsAsync(string symbol, CancellationToken ct)
    {
        return await GetBarsFromCacheAsync(symbol, "1Week", _weeklyCache, 100, ct);
    }

    private async ValueTask<IReadOnlyList<Quote>> GetBarsFromCacheAsync(
        string symbol,
        string timeframe,
        Dictionary<string, CacheEntry> cache,
        int limit,
        CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (cache.TryGetValue(symbol, out var entry))
            {
                if (DateTimeOffset.UtcNow - entry.CachedAt < CacheTtl)
                    return entry.Bars;
                cache.Remove(symbol);
            }
        }

        var bars = await marketDataClient.GetBarsAsync(symbol, timeframe, limit, ct);

        lock (_cacheLock)
        {
            cache[symbol] = new CacheEntry(bars, DateTimeOffset.UtcNow);
        }
        return bars;
    }

    private static decimal CalculateSma(IReadOnlyList<Quote> bars, int period)
    {
        if (bars.Count < period)
            return 0m;

        var sum = 0m;
        for (var i = bars.Count - period; i < bars.Count; i++)
            sum += bars[i].Close;
        return sum / period;
    }

    /// <summary>
    /// Helper to resolve per-symbol overrides with global defaults.
    /// </summary>
    private static bool? GetBool(bool globalValue, bool? symbolOverride)
        => symbolOverride ?? globalValue;
}
