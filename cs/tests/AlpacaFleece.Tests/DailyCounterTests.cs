namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for H-4: Daily risk counters written on fills.
/// Covers IncrementDailyTradeCountAsync, AddDailyRealizedPnlAsync, and ResetDailyStateAsync.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class DailyCounterTests(TradingFixture fixture) : IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        // Clean slate before each test
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "0");
        await fixture.StateRepository.SetStateAsync("trading_ready", "true");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task IncrementDailyTradeCount_FromZero_BecomesOne()
    {
        // Set the counter explicitly to "0" to simulate a fresh state
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");

        await fixture.StateRepository.IncrementDailyTradeCountAsync();

        var value = await fixture.StateRepository.GetStateAsync("daily_trade_count");
        Assert.Equal("1", value);
    }

    [Fact]
    public async Task IncrementDailyTradeCount_ThreeInvocations_BecomesThree()
    {
        await fixture.StateRepository.IncrementDailyTradeCountAsync();
        await fixture.StateRepository.IncrementDailyTradeCountAsync();
        await fixture.StateRepository.IncrementDailyTradeCountAsync();

        var value = await fixture.StateRepository.GetStateAsync("daily_trade_count");
        Assert.Equal("3", value);
    }

    [Fact]
    public async Task AddDailyRealizedPnl_NegativeDelta_AccumulatesLoss()
    {
        await fixture.StateRepository.AddDailyRealizedPnlAsync(-100m);
        await fixture.StateRepository.AddDailyRealizedPnlAsync(-50m);

        var value = await fixture.StateRepository.GetStateAsync("daily_realized_pnl");
        Assert.True(decimal.TryParse(value, out var pnl));
        Assert.Equal(-150m, pnl);
    }

    [Fact]
    public async Task ResetDailyState_ZerosBothCounters()
    {
        // Prime both counters
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "7");
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "-300");

        await fixture.StateRepository.ResetDailyStateAsync();

        var tradeCount = await fixture.StateRepository.GetStateAsync("daily_trade_count");
        var pnl = await fixture.StateRepository.GetStateAsync("daily_realized_pnl");
        Assert.Equal("0", tradeCount);
        Assert.Equal("0", pnl);
    }

    [Fact]
    public async Task RiskManager_DailyLossLimit_TriggersOnExceedance()
    {
        // Persist a daily loss that exceeds MaxDailyLoss (500)
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "-600");
        await fixture.StateRepository.SaveCircuitBreakerCountAsync(0);

        var brokerMock = Substitute.For<IBrokerService>();
        brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow.AddDays(1),
                DateTimeOffset.UtcNow.AddHours(7), DateTimeOffset.UtcNow));
        brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo("test", 50000m, 0m, 100000m, 0m, true, false, DateTimeOffset.UtcNow));
        brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        var options = new TradingOptions
        {
            RiskLimits = new RiskLimits { MaxDailyLoss = 500m, MaxTradesPerDay = 10 },
            Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
            Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
        };
        var riskManager = new RiskManager(brokerMock, fixture.StateRepository, options,
            Substitute.For<ILogger<RiskManager>>());

        var signal = new SignalEvent(
            Symbol: "AAPL", Side: "BUY", Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15), FastSma: 150m, MediumSma: 149m, SlowSma: 145m,
                Atr: 2m, Confidence: 0.8m, Regime: "TRENDING_UP",
                RegimeStrength: 0.7m, CurrentPrice: 150.5m, BarsInRegime: 15));

        var ex = await Assert.ThrowsAsync<RiskManagerException>(
            () => riskManager.CheckSignalAsync(signal).AsTask());

        Assert.Contains("Daily loss limit", ex.Message);
    }
}
