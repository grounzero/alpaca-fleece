namespace AlpacaFleece.Tests;

public sealed class VolatilityRegimeDetectorTests
{
    private readonly IMarketDataClient _marketData = Substitute.For<IMarketDataClient>();
    private readonly ILogger<VolatilityRegimeDetector> _logger = Substitute.For<ILogger<VolatilityRegimeDetector>>();

    private static TradingOptions BuildOptions() => new()
    {
        VolatilityRegime = new VolatilityRegimeOptions
        {
            Enabled = true,
            LookbackBars = 30,
            TransitionConfirmationBars = 2,
            HysteresisBuffer = 0.0002m,
            LowMaxVolatility = 0.003m,
            NormalMaxVolatility = 0.007m,
            HighMaxVolatility = 0.015m,
            LowPositionMultiplier = 1.2m,
            NormalPositionMultiplier = 1.0m,
            HighPositionMultiplier = 0.6m,
            ExtremePositionMultiplier = 0.3m,
            LowStopMultiplier = 0.8m,
            NormalStopMultiplier = 1.0m,
            HighStopMultiplier = 1.5m,
            ExtremeStopMultiplier = 2.0m
        }
    };

    [Fact]
    public void ClassifyFromVolatility_RespectsThresholdBoundaries()
    {
        var detector = new VolatilityRegimeDetector(_marketData, BuildOptions(), _logger);

        var low = detector.ClassifyFromVolatility("AAPL", 0.0020m);
        var normal = detector.ClassifyFromVolatility("MSFT", 0.0050m);
        var high = detector.ClassifyFromVolatility("NVDA", 0.0120m);
        var extreme = detector.ClassifyFromVolatility("TSLA", 0.0200m);

        Assert.Equal(VolatilityRegime.Low, low.Regime);
        Assert.Equal(VolatilityRegime.Normal, normal.Regime);
        Assert.Equal(VolatilityRegime.High, high.Regime);
        Assert.Equal(VolatilityRegime.Extreme, extreme.Regime);
    }

    [Fact]
    public void ClassifyFromVolatility_AppliesHysteresisAndConfirmation()
    {
        var detector = new VolatilityRegimeDetector(_marketData, BuildOptions(), _logger);

        // Start in LOW.
        var low = detector.ClassifyFromVolatility("AAPL", 0.0028m);
        // Slightly above low threshold but inside hysteresis -> should remain LOW.
        var lowSticky = detector.ClassifyFromVolatility("AAPL", 0.0031m);
        // Clearly in NORMAL, first candidate bar -> still LOW due confirmation.
        var pending = detector.ClassifyFromVolatility("AAPL", 0.0040m);
        // Second consecutive NORMAL candidate -> transition confirmed.
        var confirmed = detector.ClassifyFromVolatility("AAPL", 0.0042m);

        Assert.Equal(VolatilityRegime.Low, low.Regime);
        Assert.Equal(VolatilityRegime.Low, lowSticky.Regime);
        Assert.Equal(VolatilityRegime.Low, pending.Regime);
        Assert.Equal(VolatilityRegime.Normal, confirmed.Regime);
        Assert.Equal(1, confirmed.BarsInRegime);
    }

    [Fact]
    public void ClassifyFromVolatility_MapsRegimeMultipliers()
    {
        var detector = new VolatilityRegimeDetector(_marketData, BuildOptions(), _logger);

        var low = detector.ClassifyFromVolatility("AAPL", 0.002m);
        var high = detector.ClassifyFromVolatility("NVDA", 0.012m);
        // confirm transition to extreme with second above-high-threshold sample
        var extremePending = detector.ClassifyFromVolatility("NVDA", 0.020m);
        var extreme = detector.ClassifyFromVolatility("NVDA", 0.021m);

        Assert.Equal(1.2m, low.PositionMultiplier);
        Assert.Equal(0.8m, low.StopMultiplier);
        Assert.Equal(VolatilityRegime.High, high.Regime);
        Assert.Equal(0.6m, high.PositionMultiplier);
        Assert.Equal(1.5m, high.StopMultiplier);
        Assert.Equal(VolatilityRegime.High, extremePending.Regime);
        Assert.Equal(0.3m, extreme.PositionMultiplier);
        Assert.Equal(2.0m, extreme.StopMultiplier);
    }

    [Fact]
    public async Task GetRegimeAsync_WithInsufficientBars_FallsBackToNormalWithoutLowStateSeed()
    {
        var options = BuildOptions();
        var detector = new VolatilityRegimeDetector(_marketData, options, _logger);

        var bars = new List<Quote>
        {
            new("AAPL", DateTimeOffset.UtcNow, 100m, 100m, 100m, 100m, 1000)
        };
        _marketData.GetBarsAsync("AAPL", "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(bars.AsReadOnly()));

        var fallback = await detector.GetRegimeAsync("AAPL");
        var firstClassification = detector.ClassifyFromVolatility("AAPL", 0.012m);

        Assert.Equal(VolatilityRegime.Normal, fallback.Regime);
        Assert.Equal(1.0m, fallback.PositionMultiplier);
        Assert.Equal(1.0m, fallback.StopMultiplier);
        Assert.Equal(VolatilityRegime.High, firstClassification.Regime);
    }

    [Fact]
    public void ClassifyFromVolatility_UsesAssetClassOverrides()
    {
        var options = BuildOptions();
        options.Symbols = new SymbolLists
        {
            CryptoSymbols = ["BTC/USD"]
        };
        options.VolatilityRegime.Equity = new VolatilityRegimeProfileOptions
        {
            LowMaxVolatility = 0.0015m,
            NormalMaxVolatility = 0.0030m,
            HighMaxVolatility = 0.0060m
        };
        options.VolatilityRegime.Crypto = new VolatilityRegimeProfileOptions
        {
            LowMaxVolatility = 0.0040m,
            NormalMaxVolatility = 0.0080m,
            HighMaxVolatility = 0.0140m,
            LowPositionMultiplier = 0.95m,
            LowStopMultiplier = 1.05m
        };

        var detector = new VolatilityRegimeDetector(_marketData, options, _logger);

        var equity = detector.ClassifyFromVolatility("AAPL", 0.0035m);
        var crypto = detector.ClassifyFromVolatility("BTC/USD", 0.0035m);

        Assert.Equal(VolatilityRegime.High, equity.Regime);
        Assert.Equal(VolatilityRegime.Low, crypto.Regime);
        Assert.Equal(0.95m, crypto.PositionMultiplier);
        Assert.Equal(1.05m, crypto.StopMultiplier);
    }

    [Fact]
    public async Task GetRegimeAsync_SameLatestBar_DoesNotIncrementBarsInRegime()
    {
        var options = BuildOptions();
        var detector = new VolatilityRegimeDetector(_marketData, options, _logger);

        var t0 = DateTimeOffset.UtcNow;
        var lowBars = new List<Quote>
        {
            new("AAPL", t0.AddMinutes(-2), 100m, 100m, 100m, 100m, 1000),
            new("AAPL", t0.AddMinutes(-1), 100.10m, 100.10m, 100.10m, 100.10m, 1000),
            new("AAPL", t0, 100.00m, 100.00m, 100.00m, 100.00m, 1000),
        }.AsReadOnly();

        _marketData.GetBarsAsync("AAPL", "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                new ValueTask<IReadOnlyList<Quote>>(lowBars),
                new ValueTask<IReadOnlyList<Quote>>(lowBars));

        var first = await detector.GetRegimeAsync("AAPL");
        var second = await detector.GetRegimeAsync("AAPL");

        Assert.Equal(VolatilityRegime.Low, first.Regime);
        Assert.Equal(1, first.BarsInRegime);
        Assert.Equal(VolatilityRegime.Low, second.Regime);
        Assert.Equal(1, second.BarsInRegime);
    }

    [Fact]
    public async Task GetRegimeAsync_SameLatestBar_OppositeVolatilityDoesNotAdvanceTransition()
    {
        var options = BuildOptions();
        options.VolatilityRegime.TransitionConfirmationBars = 2;
        var detector = new VolatilityRegimeDetector(_marketData, options, _logger);

        var t0 = DateTimeOffset.UtcNow;
        var lowBars = new List<Quote>
        {
            new("AAPL", t0.AddMinutes(-2), 100m, 100m, 100m, 100m, 1000),
            new("AAPL", t0.AddMinutes(-1), 100.10m, 100.10m, 100.10m, 100.10m, 1000),
            new("AAPL", t0, 100.00m, 100.00m, 100.00m, 100.00m, 1000),
        }.AsReadOnly();

        // Same latest timestamp as lowBars, but much higher realised volatility.
        var extremeSameBar = new List<Quote>
        {
            new("AAPL", t0.AddMinutes(-2), 100m, 100m, 100m, 100m, 1000),
            new("AAPL", t0.AddMinutes(-1), 104m, 104m, 104m, 104m, 1000),
            new("AAPL", t0, 96m, 96m, 96m, 96m, 1000),
        }.AsReadOnly();

        _marketData.GetBarsAsync("AAPL", "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(
                new ValueTask<IReadOnlyList<Quote>>(lowBars),
                new ValueTask<IReadOnlyList<Quote>>(extremeSameBar));

        var first = await detector.GetRegimeAsync("AAPL");
        var second = await detector.GetRegimeAsync("AAPL");

        Assert.Equal(VolatilityRegime.Low, first.Regime);
        Assert.Equal(VolatilityRegime.Low, second.Regime);
        Assert.Equal(1, second.BarsInRegime);
    }
}
