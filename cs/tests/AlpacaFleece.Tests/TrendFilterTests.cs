namespace AlpacaFleece.Tests;

/// <summary>
/// Unit tests for TrendFilter and VolumeFilter.
/// </summary>
public sealed class TrendFilterTests
{
    private readonly IMarketDataClient _marketData = Substitute.For<IMarketDataClient>();
    private readonly ILogger<TrendFilter> _trendLogger = Substitute.For<ILogger<TrendFilter>>();
    private readonly ILogger<VolumeFilter> _volumeLogger = Substitute.For<ILogger<VolumeFilter>>();

    // ─── Helpers ─────────────────────────────────────────────────────────────────

    private static TradingOptions FilterOptions(
        bool enableTrend = true,
        int smaPeriod = 5,
        bool enableVolume = true,
        int volumeLookback = 5,
        decimal volumeMultiplier = 1.5m) => new()
    {
        SignalFilters = new SignalFilterOptions
        {
            EnableDailyTrendFilter = enableTrend,
            DailySmaPeriod = smaPeriod,
            EnableVolumeFilter = enableVolume,
            VolumeLookbackPeriod = volumeLookback,
            VolumeMultiplier = volumeMultiplier,
        },
    };

    /// <summary>
    /// Builds a list of daily Quote bars where closes are provided.
    /// Volume is fixed at 1,000,000 for simplicity.
    /// </summary>
    private static IReadOnlyList<Quote> MakeDailyBars(string symbol, params decimal[] closes)
    {
        var bars = new List<Quote>();
        var baseDate = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < closes.Length; i++)
        {
            var c = closes[i];
            bars.Add(new Quote(symbol, baseDate.AddDays(i), c, c + 1m, c - 1m, c, 1_000_000));
        }
        return bars.AsReadOnly();
    }

    private TrendFilter CreateTrendFilter(TradingOptions? opts = null) =>
        new(_marketData, opts ?? FilterOptions(), _trendLogger);

    private VolumeFilter CreateVolumeFilter(TradingOptions? opts = null) =>
        new(opts ?? FilterOptions(), _volumeLogger);

    // ─── TrendFilter ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TrendFilter_Long_PassesWhen_PriceAboveDailySma()
    {
        // SMA(5) of closes [10,11,12,13,14] = 12. Last close = 14 > 12 → BUY passes.
        var bars = MakeDailyBars("AAPL", 10m, 11m, 12m, 13m, 14m, 15m);
        _marketData.GetBarsAsync("AAPL", "1Day", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(bars));

        var filter = CreateTrendFilter();
        var result = await filter.CheckAsync("AAPL", "BUY");

        Assert.True(result);
    }

    [Fact]
    public async Task TrendFilter_Long_BlocksWhen_PriceBelowDailySma()
    {
        // SMA(5) of closes [100,99,98,97,96] = 98. Last close = 95 < 98 → BUY blocked.
        var bars = MakeDailyBars("AAPL", 100m, 99m, 98m, 97m, 96m, 95m);
        _marketData.GetBarsAsync("AAPL", "1Day", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(bars));

        var filter = CreateTrendFilter();
        var result = await filter.CheckAsync("AAPL", "BUY");

        Assert.False(result);
    }

    [Fact]
    public async Task TrendFilter_Short_PassesWhen_PriceBelowDailySma()
    {
        // SMA(5) of closes [100,99,98,97,96] = 98. Last close = 95 < 98 → SELL passes.
        var bars = MakeDailyBars("AAPL", 100m, 99m, 98m, 97m, 96m, 95m);
        _marketData.GetBarsAsync("AAPL", "1Day", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(bars));

        var filter = CreateTrendFilter();
        var result = await filter.CheckAsync("AAPL", "SELL");

        Assert.True(result);
    }

    [Fact]
    public async Task TrendFilter_Short_BlocksWhen_PriceAboveDailySma()
    {
        // SMA(5) of closes [10,11,12,13,14] = 12. Last close = 14 > 12 → SELL blocked.
        var bars = MakeDailyBars("AAPL", 10m, 11m, 12m, 13m, 14m, 15m);
        _marketData.GetBarsAsync("AAPL", "1Day", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(bars));

        var filter = CreateTrendFilter();
        var result = await filter.CheckAsync("AAPL", "SELL");

        Assert.False(result);
    }

    [Fact]
    public async Task TrendFilter_PassesWhen_Disabled()
    {
        // Filter disabled — API should not be called and result must be true.
        var opts = FilterOptions(enableTrend: false);
        var filter = CreateTrendFilter(opts);

        var result = await filter.CheckAsync("AAPL", "BUY");

        Assert.True(result);
        await _marketData.DidNotReceive()
            .GetBarsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrendFilter_PassesWhen_InsufficientDailyHistory()
    {
        // Only 2 bars returned, SMA period = 5 → insufficient → pass-through.
        var bars = MakeDailyBars("AAPL", 100m, 90m);
        _marketData.GetBarsAsync("AAPL", "1Day", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(bars));

        var filter = CreateTrendFilter();
        var result = await filter.CheckAsync("AAPL", "BUY");

        Assert.True(result);
    }

    // ─── VolumeFilter ────────────────────────────────────────────────────────────

    private static IReadOnlyList<(decimal Open, decimal High, decimal Low, decimal Close, long Volume)>
        MakeVolumeBars(params long[] volumes)
    {
        var bars = new List<(decimal, decimal, decimal, decimal, long)>();
        foreach (var v in volumes)
            bars.Add((100m, 101m, 99m, 100m, v));
        return bars.AsReadOnly();
    }

    [Fact]
    public void VolumeFilter_PassesWhen_VolumeAboveThreshold()
    {
        // Volumes: [1000, 1000, 1000, 1000, 3000]. Avg(5) = 1400. 3000 >= 1400 * 1.5 = 2100 → pass.
        var bars = MakeVolumeBars(1000, 1000, 1000, 1000, 3000);
        var filter = CreateVolumeFilter();

        var result = filter.Check(bars);

        Assert.True(result);
    }

    [Fact]
    public void VolumeFilter_BlocksWhen_VolumeBelowThreshold()
    {
        // Volumes: [2000, 2000, 2000, 2000, 500]. Avg(5) = 1700. 500 < 1700 * 1.5 = 2550 → block.
        var bars = MakeVolumeBars(2000, 2000, 2000, 2000, 500);
        var filter = CreateVolumeFilter();

        var result = filter.Check(bars);

        Assert.False(result);
    }

    [Fact]
    public void VolumeFilter_PassesWhen_Disabled()
    {
        // Low volume bars — but filter is disabled so result must be true.
        var bars = MakeVolumeBars(1, 1, 1, 1, 1);
        var opts = FilterOptions(enableVolume: false);
        var filter = CreateVolumeFilter(opts);

        var result = filter.Check(bars);

        Assert.True(result);
    }

    [Fact]
    public void VolumeFilter_PassesWhen_InsufficientHistory()
    {
        // Only 3 bars, lookback = 5 → insufficient → pass-through.
        var bars = MakeVolumeBars(1, 1, 1);
        var filter = CreateVolumeFilter();

        var result = filter.Check(bars);

        Assert.True(result);
    }
}
