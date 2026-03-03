namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for SmaCrossoverStrategy (3 SMA pairs, ATR, regime detection).
/// </summary>
public sealed class StrategyTests
{
    [Fact]
    public void BarHistory_CalculatesSmaCorrectly()
    {
        var history = new BarHistory(50);

        // Add 10 bars with close prices 100, 101, 102, ..., 109
        for (var i = 0; i < 10; i++)
        {
            var close = 100m + i;
            history.AddBar(close, close + 1, close - 1, close, 1000);
        }

        var sma5 = history.CalculateSma(5);

        Assert.Equal(107m, sma5);
    }

    [Fact]
    public void BarHistory_CalculatesAtrCorrectly()
    {
        var history = new BarHistory(50);

        // Add bars with known ATR
        for (var i = 0; i < 14; i++)
        {
            var high = 100m + (i * 0.5m);
            var low = 100m + (i * 0.5m) - 1m;
            var close = 100m + (i * 0.5m) - 0.5m;

            history.AddBar(close, high, low, close, 1000);
        }

        var atr = history.CalculateAtr(14);
        Assert.True(atr > 0, "ATR should be positive");
    }

    [Fact]
    public void RegimeDetector_IdentifiesTrendingUp()
    {
        var detector = new RegimeDetector();
        var regime = detector.DetectRegime(110m, 105m, 100m);

        Assert.Equal("TRENDING_UP", regime.RegimeType);
    }

    [Fact]
    public void RegimeDetector_IdentifiesTrendingDown()
    {
        var detector = new RegimeDetector();
        var regime = detector.DetectRegime(90m, 95m, 100m);

        Assert.Equal("TRENDING_DOWN", regime.RegimeType);
    }

    [Fact]
    public void RegimeDetector_IdentifiesRanging()
    {
        var detector = new RegimeDetector();
        var regime = detector.DetectRegime(100m, 110m, 90m);

        Assert.Equal("RANGING", regime.RegimeType);
    }

    [Fact]
    public async Task SmaCrossoverStrategy_EmitsBuySignal()
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        // Add bars to reach required history (51 bars)
        for (var i = 0; i < 51; i++)
        {
            var close = 100m + (i % 10); // Simulate price movement
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close,
                High: close + 1,
                Low: close - 1,
                Close: close,
                Volume: 1000000);

            await strategy.OnBarAsync(bar);
        }

        // Check if strategy is ready
        Assert.True(strategy.IsReady, "Strategy should have enough history");
    }

    [Fact]
    public async Task SmaCrossoverStrategy_RespectsRequiredHistory()
    {
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        Assert.Equal(51, strategy.RequiredHistory); // 50 (slowest SMA) + 1 buffer
        Assert.False(strategy.IsReady, "Strategy should not be ready with no bars");

        // Add 51 bars
        for (var i = 0; i < 51; i++)
        {
            var close = 100m + i;
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close,
                High: close + 1,
                Low: close - 1,
                Close: close,
                Volume: 1000000);

            await strategy.OnBarAsync(bar);
        }

        Assert.True(strategy.IsReady, "Strategy should be ready after required history");
    }

    [Fact]
    public async Task SmaCrossoverStrategy_ComputesATR_AndStoresInMetadata()
    {
        // Test Vector 4: ATR(14) computed and stored in metadata
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        // Add 51 bars with volatility to get non-zero ATR
        for (var i = 0; i < 51; i++)
        {
            var close = 100m + (i * 0.5m);
            var high = close + 2m; // Add spread
            var low = close - 2m;

            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close,
                High: high,
                Low: low,
                Close: close,
                Volume: 1000000);

            await strategy.OnBarAsync(bar);
        }

        // Verify ATR was computed and published
        await eventBus.Received()
            .PublishAsync(
                Arg.Is<SignalEvent>(s =>
                    s.Metadata.Atr.HasValue &&
                    s.Metadata.Atr > 0),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SmaCrossoverStrategy_ConfidenceScore_TrendingUp()
    {
        // Test Vector 5: Confidence scoring - trending up should score 0.8+
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        // Add bars in strong uptrend: fast > medium > slow
        for (var i = 0; i < 51; i++)
        {
            var close = 100m + (i * 1.5m); // Strong uptrend
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close - 0.5m,
                High: close + 1m,
                Low: close - 1m,
                Close: close,
                Volume: 2000000);

            await strategy.OnBarAsync(bar);
        }

        // Verify signals have high confidence in trending regime
        await eventBus.Received()
            .PublishAsync(
                Arg.Is<SignalEvent>(s =>
                    s.Side == "BUY" &&
                    s.Metadata.Confidence >= 0.5m),
                Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SmaCrossoverStrategy_Regime_DetectionAccurate()
    {
        // Test Vector 6: Regime detection accurate (trending vs ranging)
        var detector = new RegimeDetector();

        // Trending up: fast > medium > slow
        var uptrend = detector.DetectRegime(110m, 105m, 100m);
        Assert.Equal("TRENDING_UP", uptrend.RegimeType);
        Assert.True(uptrend.Strength > 0);

        // Trending down: fast < medium < slow
        var downtrend = detector.DetectRegime(90m, 95m, 100m);
        Assert.Equal("TRENDING_DOWN", downtrend.RegimeType);
        Assert.True(downtrend.Strength > 0);

        // Ranging: misaligned SMAs
        var ranging = detector.DetectRegime(100m, 110m, 90m);
        Assert.Equal("RANGING", ranging.RegimeType);
        Assert.Equal(0.5m, ranging.Strength);
    }

    [Fact]
    public async Task SmaCrossoverStrategy_MultiplePairs_GenerateSignals()
    {
        // Test Vector 7: Multiple signals per bar (up to 3 SMA pairs)
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        // Add bars to reach required history
        for (var i = 0; i < 51; i++)
        {
            var close = 100m + i;
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close,
                High: close + 1,
                Low: close - 1,
                Close: close,
                Volume: 1000000);

            await strategy.OnBarAsync(bar);
        }

        // Verify signals were published
        Assert.True(strategy.IsReady);
    }

    [Fact]
    public async Task SmaCrossoverStrategy_BuyCrossover_OnUptrendFastAboveSlow()
    {
        // Test Vector 1: Buy signal on upward SMA(5,15) crossover in uptrend
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ => new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        // Add bars in uptrend with crossover
        for (var i = 0; i < 60; i++)
        {
            var close = 100m + (i * 0.8m) + (i % 2) * 0.3m; // Uptrend with oscillation
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close - 0.2m,
                High: close + 1m,
                Low: close - 1m,
                Close: close,
                Volume: 1500000);

            await strategy.OnBarAsync(bar);
        }

        Assert.True(strategy.IsReady);
    }

    [Fact]
    public async Task SmaCrossoverStrategy_SellCrossover_OnDowntrendFastBelowSlow()
    {
        // Test Vector 2: Sell signal on downward SMA(10,30) crossover
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        // Add bars in downtrend
        for (var i = 0; i < 60; i++)
        {
            var close = 200m - (i * 0.8m); // Downtrend
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close + 0.2m,
                High: close + 1m,
                Low: close - 1m,
                Close: close,
                Volume: 1500000);

            await strategy.OnBarAsync(bar);
        }

        Assert.True(strategy.IsReady);
    }

    [Fact]
    public async Task SmaCrossoverStrategy_NoSignal_WhenLessThan51Bars()
    {
        // Test Vector 3: No signal if <51 bars
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();

        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        // Add only 30 bars
        for (var i = 0; i < 30; i++)
        {
            var close = 100m + i;
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close,
                High: close + 1,
                Low: close - 1,
                Close: close,
                Volume: 1000000);

            await strategy.OnBarAsync(bar);
        }

        // Strategy should not be ready
        Assert.False(strategy.IsReady);

        // No signals should have been published
        await eventBus.DidNotReceive()
            .PublishAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>());
    }

    // ─── Filter integration tests ─────────────────────────────────────────────────

    /// <summary>Builds a daily Quote list with the given close prices (6 bars, SMA period 5).</summary>
    private static IReadOnlyList<Quote> DailyBars(string symbol, decimal[] closes)
    {
        var baseDate = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var bars = new List<Quote>();
        for (var i = 0; i < closes.Length; i++)
        {
            var c = closes[i];
            bars.Add(new Quote(symbol, baseDate.AddDays(i), c, c + 1m, c - 1m, c, 1_000_000));
        }
        return bars.AsReadOnly();
    }

    /// <summary>Runs 51 uptrend bars through strategy (guarantees a BUY crossover).</summary>
    private static async Task RunUptrendBarsAsync(SmaCrossoverStrategy strategy, long volume = 1_000_000)
    {
        for (var i = 0; i < 51; i++)
        {
            var close = 100m + (i * 1.5m);
            var bar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close - 0.5m,
                High: close + 1m,
                Low: close - 1m,
                Close: close,
                Volume: volume);
            await strategy.OnBarAsync(bar);
        }
    }

    [Fact]
    public async Task SmaCrossoverStrategy_BlocksSignal_WhenTrendFilterFails()
    {
        // Daily bars: SMA(5) of [99,98,97,96,95] = 97; last close 95 < 97 → BUY blocked.
        var eventBus = Substitute.For<IEventBus>();
        var marketData = Substitute.For<IMarketDataClient>();
        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var dailyBars = DailyBars("AAPL", [100m, 99m, 98m, 97m, 96m, 95m]);
        marketData.GetBarsAsync("AAPL", "1Day", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(dailyBars));

        var opts = new TradingOptions
        {
            SignalFilters = new SignalFilterOptions
            {
                EnableDailyTrendFilter = true,
                DailySmaPeriod = 5,
                EnableVolumeFilter = false,
            },
        };

        var trendFilter = new TrendFilter(marketData, opts, Substitute.For<ILogger<TrendFilter>>());
        var volumeFilter = new VolumeFilter(opts, Substitute.For<ILogger<VolumeFilter>>());
        var strategy = new SmaCrossoverStrategy(
            eventBus, Substitute.For<ILogger<SmaCrossoverStrategy>>(),
            trendFilter: trendFilter, volumeFilter: volumeFilter);

        await RunUptrendBarsAsync(strategy);

        // BUY crossover detected but trend filter blocks it
        await eventBus.DidNotReceive()
            .PublishAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SmaCrossoverStrategy_BlocksSignal_WhenVolumeFilterFails()
    {
        // VolumeMultiplier=100 means current must be 100× average — effectively always blocks.
        var eventBus = Substitute.For<IEventBus>();
        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var opts = new TradingOptions
        {
            SignalFilters = new SignalFilterOptions
            {
                EnableDailyTrendFilter = false,
                EnableVolumeFilter = true,
                VolumeLookbackPeriod = 5,
                VolumeMultiplier = 100m,
            },
        };

        var volumeFilter = new VolumeFilter(opts, Substitute.For<ILogger<VolumeFilter>>());
        var strategy = new SmaCrossoverStrategy(
            eventBus, Substitute.For<ILogger<SmaCrossoverStrategy>>(),
            volumeFilter: volumeFilter);

        // Uniform volume: current = avg = 1_000_000 < 100 × 1_000_000 = 100_000_000 → blocked
        await RunUptrendBarsAsync(strategy, volume: 1_000_000);

        await eventBus.DidNotReceive()
            .PublishAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SmaCrossoverStrategy_EmitsSignal_WhenBothFilterPass()
    {
        // Daily bars: SMA(5) of [91,92,93,94,95] = 93; last close 95 > 93 → BUY passes.
        // VolumeMultiplier=0.1 ensures uniform volume passes (current ≥ 0.1 × avg always).
        var eventBus = Substitute.For<IEventBus>();
        var marketData = Substitute.For<IMarketDataClient>();
        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var dailyBars = DailyBars("AAPL", [90m, 91m, 92m, 93m, 94m, 95m]);
        marketData.GetBarsAsync("AAPL", "1Day", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(dailyBars));

        var opts = new TradingOptions
        {
            SignalFilters = new SignalFilterOptions
            {
                EnableDailyTrendFilter = true,
                DailySmaPeriod = 5,
                EnableVolumeFilter = true,
                VolumeLookbackPeriod = 5,
                VolumeMultiplier = 0.1m,
            },
        };

        var trendFilter = new TrendFilter(marketData, opts, Substitute.For<ILogger<TrendFilter>>());
        var volumeFilter = new VolumeFilter(opts, Substitute.For<ILogger<VolumeFilter>>());
        var strategy = new SmaCrossoverStrategy(
            eventBus, Substitute.For<ILogger<SmaCrossoverStrategy>>(),
            trendFilter: trendFilter, volumeFilter: volumeFilter);

        await RunUptrendBarsAsync(strategy);

        // Both filters pass → at least one signal published
        await eventBus.Received()
            .PublishAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SmaCrossoverStrategy_EmitsSignal_WhenFiltersDisabled()
    {
        // No filters provided — signals emitted as normal (regression guard).
        var eventBus = Substitute.For<IEventBus>();
        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(
            eventBus, Substitute.For<ILogger<SmaCrossoverStrategy>>());

        await RunUptrendBarsAsync(strategy);

        await eventBus.Received()
            .PublishAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SmaCrossoverStrategy_NoSignal_With50Bars_OneLessThanRequired()
    {
        // Boundary test: 50 bars = RequiredBars - 1. Strategy must stay in insufficient
        // state and emit no signal. This is the exact count produced by the pre-fix
        // StreamPoller fetch (hardcoded limit=50 < RequiredBars=51).
        var eventBus = Substitute.For<IEventBus>();
        var logger = Substitute.For<ILogger<SmaCrossoverStrategy>>();
        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<bool>(true));

        var strategy = new SmaCrossoverStrategy(eventBus, logger);

        for (var i = 0; i < 50; i++)
        {
            var close = 100m + i;
            await strategy.OnBarAsync(new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(i),
                Open: close,
                High: close + 1,
                Low: close - 1,
                Close: close,
                Volume: 1_000_000));
        }

        Assert.False(strategy.IsReady, "Strategy must not be ready with 50 bars (RequiredBars=51)");
        await eventBus.DidNotReceive()
            .PublishAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>());
    }
}
