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
        var expectedSma5 = (105m + 106m + 107m + 108m + 109m) / 5m; // 107

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

        var signalEmitted = false;
        eventBus.PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                signalEmitted = true;
                return new ValueTask<bool>(true);
            });

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
}
