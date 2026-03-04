using Alpaca.Markets;
using AlpacaFleece.Infrastructure.Symbols;

namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for exit pricing pipeline:
///   - MarketDataClient.GetSnapshotAsync using GetBarsAsync (bar-based price)
///   - Freshness validation (MaxPriceAgeSeconds)
///   - ExitManager consecutive-failure tracking → market_data_degraded KV
///   - RiskManager SAFETY-tier block on market_data_degraded
/// </summary>
[Collection("Trading Database Collection")]
public sealed class ExitPricingTests(TradingFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Clear market_data_degraded KV so subsequent tests start clean
        await fixture.StateRepository.SetStateAsync("market_data_degraded", "false");
    }

    // ── MarketDataClient snapshot via bars ────────────────────────────────────

    [Fact]
    public async Task GetSnapshotAsync_UsesFreshBar_ReturnsMidPrice()
    {
        // Arrange: equity data client returns a 1-min bar with close=150m
        var equityMock = Substitute.For<IAlpacaDataClient>();
        var freshTs = DateTimeOffset.UtcNow.AddSeconds(-30);

        var bar = Substitute.For<IBar>();
        bar.Close.Returns(150m);
        bar.Open.Returns(149m);
        bar.High.Returns(151m);
        bar.Low.Returns(148m);
        bar.Volume.Returns(5000m);
        bar.TimeUtc.Returns(freshTs.UtcDateTime);

        var page = Substitute.For<IPage<IBar>>();
        page.Items.Returns(new List<IBar> { bar }.AsReadOnly());
        equityMock
            .ListHistoricalBarsAsync(Arg.Any<HistoricalBarsRequest>(), Arg.Any<CancellationToken>())
            .Returns(page);

        var sc = new SymbolClassifier(new List<string>(), new List<string> { "AAPL" });
        var client = new MarketDataClient(
            equityMock,
            Substitute.For<IAlpacaCryptoDataClient>(),
            new BrokerOptions { ApiKey = "k", SecretKey = "s", IsPaperTrading = true },
            Substitute.For<ILogger<MarketDataClient>>(),
            sc,
            maxPriceAgeSeconds: 300);

        // Act
        var snapshot = await client.GetSnapshotAsync("AAPL");

        // Assert: MidPrice = (bid + ask) / 2 = (close + close) / 2 = close
        Assert.Equal(150m, snapshot.MidPrice);
        Assert.Equal(150m, snapshot.BidPrice);
        Assert.Equal(150m, snapshot.AskPrice);
        Assert.Equal("AAPL", snapshot.Symbol);
    }

    [Fact]
    public async Task GetSnapshotAsync_StaleBar_ThrowsMarketDataException()
    {
        // Arrange: bar is 10 minutes old, threshold = 300s
        var equityMock = Substitute.For<IAlpacaDataClient>();
        var staleTs = DateTimeOffset.UtcNow.AddMinutes(-10);

        var bar = Substitute.For<IBar>();
        bar.Close.Returns(150m);
        bar.Open.Returns(149m);
        bar.High.Returns(151m);
        bar.Low.Returns(148m);
        bar.Volume.Returns(5000m);
        bar.TimeUtc.Returns(staleTs.UtcDateTime);

        var page = Substitute.For<IPage<IBar>>();
        page.Items.Returns(new List<IBar> { bar }.AsReadOnly());
        equityMock
            .ListHistoricalBarsAsync(Arg.Any<HistoricalBarsRequest>(), Arg.Any<CancellationToken>())
            .Returns(page);

        var sc = new SymbolClassifier(new List<string>(), new List<string> { "AAPL" });
        var client = new MarketDataClient(
            equityMock,
            Substitute.For<IAlpacaCryptoDataClient>(),
            new BrokerOptions { ApiKey = "k", SecretKey = "s", IsPaperTrading = true },
            Substitute.For<ILogger<MarketDataClient>>(),
            sc,
            maxPriceAgeSeconds: 300);

        // Act & Assert
        await Assert.ThrowsAsync<MarketDataException>(
            () => client.GetSnapshotAsync("AAPL").AsTask());
    }

    [Fact]
    public async Task GetSnapshotAsync_FreshnessDisabled_DoesNotThrowForStaleBar()
    {
        // Arrange: bar is very old but MaxPriceAgeSeconds = 0 (disabled)
        var equityMock = Substitute.For<IAlpacaDataClient>();
        var staleTs = DateTimeOffset.UtcNow.AddHours(-24);

        var bar = Substitute.For<IBar>();
        bar.Close.Returns(100m);
        bar.Open.Returns(99m);
        bar.High.Returns(101m);
        bar.Low.Returns(98m);
        bar.Volume.Returns(1000m);
        bar.TimeUtc.Returns(staleTs.UtcDateTime);

        var page = Substitute.For<IPage<IBar>>();
        page.Items.Returns(new List<IBar> { bar }.AsReadOnly());
        equityMock
            .ListHistoricalBarsAsync(Arg.Any<HistoricalBarsRequest>(), Arg.Any<CancellationToken>())
            .Returns(page);

        var sc = new SymbolClassifier(new List<string>(), new List<string> { "AAPL" });
        var client = new MarketDataClient(
            equityMock,
            Substitute.For<IAlpacaCryptoDataClient>(),
            new BrokerOptions { ApiKey = "k", SecretKey = "s", IsPaperTrading = true },
            Substitute.For<ILogger<MarketDataClient>>(),
            sc,
            maxPriceAgeSeconds: 0); // disabled

        // Act & Assert: should not throw
        var snapshot = await client.GetSnapshotAsync("AAPL");
        Assert.Equal(100m, snapshot.MidPrice);
    }

    // ── ExitManager failure tracking ──────────────────────────────────────────

    [Fact]
    public async Task ExitManager_OnPriceFailure_SetsKvFlag_After3Failures()
    {
        // Arrange: market data client always throws
        var marketDataMock = Substitute.For<IMarketDataClient>();
        marketDataMock
            .GetSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<BidAskSpread>(new MarketDataException("Stale price data")));

        var brokerMock = Substitute.For<IBrokerService>();
        brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var positionTracker = Substitute.For<IPositionTracker>();
        positionTracker.GetAllPositions()
            .Returns(new Dictionary<string, PositionData>
            {
                ["AAPL"] = new PositionData("AAPL", 10m, 100m, 2m, 97m)
            });

        var options = Options.Create(new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = ["AAPL"] }
        });

        var exitManager = new ExitManager(
            positionTracker,
            brokerMock,
            marketDataMock,
            fixture.EventBus,
            fixture.StateRepository,
            Substitute.For<ILogger<ExitManager>>(),
            options);

        // Act: run CheckPositionsAsync 3 times (each triggers a price fetch failure)
        await exitManager.CheckPositionsAsync(CancellationToken.None);
        await exitManager.CheckPositionsAsync(CancellationToken.None);
        await exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert: KV flag is set after 3 failures
        var flag = await fixture.StateRepository.GetStateAsync("market_data_degraded");
        Assert.Equal("true", flag);
    }

    [Fact]
    public async Task ExitManager_OnPriceRecovery_ClearsKvFlag()
    {
        // Pre-set the degraded flag
        await fixture.StateRepository.SetStateAsync("market_data_degraded", "true");

        // Arrange: market data mock fails 3 times, then succeeds
        var marketDataMock = Substitute.For<IMarketDataClient>();
        var callCount = 0;
        marketDataMock
            .GetSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount <= 3
                    ? ValueTask.FromException<BidAskSpread>(new MarketDataException("Stale price data"))
                    : new ValueTask<BidAskSpread>(new BidAskSpread("AAPL", 95m, 95m, 0m, 0m, DateTimeOffset.UtcNow));
            });

        var brokerMock = Substitute.For<IBrokerService>();
        brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var positionTracker = Substitute.For<IPositionTracker>();
        positionTracker.GetAllPositions()
            .Returns(new Dictionary<string, PositionData>
            {
                ["AAPL"] = new PositionData("AAPL", 10m, 100m, 2m, 97m)
            });

        var options = Options.Create(new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = ["AAPL"] }
        });

        var exitManager = new ExitManager(
            positionTracker,
            brokerMock,
            marketDataMock,
            fixture.EventBus,
            fixture.StateRepository,
            Substitute.For<ILogger<ExitManager>>(),
            options);

        // Trip the counter 3 times (flag should be set)
        await exitManager.CheckPositionsAsync(CancellationToken.None);
        await exitManager.CheckPositionsAsync(CancellationToken.None);
        await exitManager.CheckPositionsAsync(CancellationToken.None);

        // Now succeed once → flag should clear
        await exitManager.CheckPositionsAsync(CancellationToken.None);

        var flag = await fixture.StateRepository.GetStateAsync("market_data_degraded");
        Assert.NotEqual("true", flag);
    }

    // ── RiskManager SAFETY-tier block ─────────────────────────────────────────

    [Fact]
    public async Task RiskManager_BlocksEntries_WhenMarketDataDegraded()
    {
        // Arrange: set market_data_degraded=true and trading_ready=true
        await fixture.StateRepository.SetStateAsync("market_data_degraded", "true");
        await fixture.StateRepository.SetStateAsync("trading_ready", "true");

        var brokerMock = Substitute.For<IBrokerService>();
        brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var options = new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = ["AAPL"] }
        };

        var riskManager = new RiskManager(
            brokerMock,
            fixture.StateRepository,
            options,
            Substitute.For<ILogger<RiskManager>>());

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m,
                BarsInRegime: 15));

        // Act & Assert: RiskManagerException thrown from SAFETY tier
        var ex = await Assert.ThrowsAsync<RiskManagerException>(
            () => riskManager.CheckSignalAsync(signal).AsTask());

        Assert.Contains("Market data degraded", ex.Message);
    }
}
