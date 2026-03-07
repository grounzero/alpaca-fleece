namespace AlpacaFleece.Tests;

/// <summary>
/// Integration tests for volatility-regime adaptive sizing across symbols.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class VolatilityAdaptationIntegrationTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly IMarketDataClient _marketDataMock = Substitute.For<IMarketDataClient>();
    private readonly ILogger<RiskManager> _riskManagerLogger = Substitute.For<ILogger<RiskManager>>();
    private readonly ILogger<OrderManager> _orderManagerLogger = Substitute.For<ILogger<OrderManager>>();
    private readonly ILogger<VolatilityRegimeDetector> _volLogger = Substitute.For<ILogger<VolatilityRegimeDetector>>();

    public async Task InitializeAsync()
    {
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "0");
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");
        await fixture.StateRepository.SetStateAsync("trading_ready", "true");
        await fixture.StateRepository.SaveCircuitBreakerCountAsync(0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task HighVsLowRegime_ChangesOrderSize()
    {
        var options = new TradingOptions
        {
            Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
            Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) },
            RiskLimits = new RiskLimits
            {
                MaxPositionSizePct = 0.05m,
                MaxRiskPerTradePct = 0.01m,
                StopLossPct = 0.02m,
                MinSignalConfidence = 0.2m
            },
            VolatilityRegime = new VolatilityRegimeOptions
            {
                Enabled = true,
                TransitionConfirmationBars = 1,
                LowMaxVolatility = 0.001m,
                NormalMaxVolatility = 0.003m,
                HighMaxVolatility = 0.020m,
                LowPositionMultiplier = 1.2m,
                HighPositionMultiplier = 0.5m
            }
        };

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow.AddHours(15), DateTimeOffset.UtcNow.AddHours(7), DateTimeOffset.UtcNow));
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo("test", 10000m, 0m, 100000m, 0m, true, false, DateTimeOffset.UtcNow));

        var lowBars = new List<Quote>();
        var lowTs = DateTimeOffset.UtcNow.AddMinutes(-31);
        decimal lowPx = 100m;
        for (var i = 0; i < 31; i++)
        {
            lowPx *= 1.00005m;
            lowBars.Add(new Quote("LOW", lowTs.AddMinutes(i), lowPx, lowPx, lowPx, lowPx, 1000));
        }

        var highBars = new List<Quote>();
        var highTs = DateTimeOffset.UtcNow.AddMinutes(-31);
        decimal highPx = 100m;
        for (var i = 0; i < 31; i++)
        {
            highPx *= i % 2 == 0 ? 1.01m : 0.99m;
            highBars.Add(new Quote("HIGH", highTs.AddMinutes(i), highPx, highPx, highPx, highPx, 1000));
        }

        _marketDataMock.GetBarsAsync("LOW", "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(lowBars.AsReadOnly()));
        _marketDataMock.GetBarsAsync("HIGH", "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(highBars.AsReadOnly()));

        var volDetector = new VolatilityRegimeDetector(_marketDataMock, options, _volLogger);
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _orderManagerLogger,
            volatilityRegimeDetector: volDetector);

        decimal qtyLow = 0m;
        decimal qtyHigh = 0m;
        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var symbol = ci.ArgAt<string>(0);
                var qty = ci.ArgAt<decimal>(2);
                if (symbol == "LOW") qtyLow = qty;
                if (symbol == "HIGH") qtyHigh = qty;
                return new OrderInfo(
                    AlpacaOrderId: $"alpaca_{symbol}",
                    ClientOrderId: ci.ArgAt<string>(4),
                    Symbol: symbol,
                    Side: ci.ArgAt<string>(1),
                    Quantity: qty,
                    FilledQuantity: 0m,
                    AverageFilledPrice: 0m,
                    Status: OrderState.Accepted,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: null);
            });

        var lowSignal = new SignalEvent(
            "LOW", "BUY", "1m", DateTimeOffset.UtcNow,
            new SignalMetadata((5, 15), 10m, 9m, 8m, 1m, 0.9m, "TRENDING_UP", 0.8m, 150m, BarsInRegime: 15));
        var highSignal = new SignalEvent(
            "HIGH", "BUY", "1m", DateTimeOffset.UtcNow.AddSeconds(1),
            new SignalMetadata((5, 15), 10m, 9m, 8m, 1m, 0.9m, "TRENDING_UP", 0.8m, 150m, BarsInRegime: 15));

        _ = await orderManager.SubmitSignalAsync(lowSignal, 0m, 150m);
        _ = await orderManager.SubmitSignalAsync(highSignal, 0m, 150m);

        Assert.True(qtyLow > qtyHigh, $"Expected low-vol qty > high-vol qty, got {qtyLow} vs {qtyHigh}");
        Assert.True(qtyLow > 0m && qtyHigh > 0m);
    }
}
