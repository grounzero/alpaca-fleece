namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for OrderManager (SHA-256 idempotency, persist-before-submit).
/// </summary>
[Collection("Trading Database Collection")]
public sealed class OrderManagerTests(TradingFixture fixture)
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly ILogger<OrderManager> _logger = Substitute.For<ILogger<OrderManager>>();

    [Fact]
    public async Task SubmitSignalAsync_GeneratesDeterministicClientOrderId()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: null, RiskTier: "FILTERS"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2024-02-21T10:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150.5m));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_123",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        // Same input should generate same ID
        var clientOrderId2 = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        Assert.NotEmpty(clientOrderId);
        Assert.Equal(16, clientOrderId.Length); // First 16 chars of SHA256
        Assert.Equal(clientOrderId, clientOrderId2);
    }

    [Fact]
    public async Task SubmitSignalAsync_PersistsBeforeSubmission()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: null, RiskTier: "FILTERS"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

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
                CurrentPrice: 150.5m));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_456",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        // Check that intent was persisted
        var intent = await fixture.StateRepository.GetOrderIntentAsync(clientOrderId);
        Assert.NotNull(intent);
        Assert.Equal("AAPL", intent.Symbol);
        Assert.Equal("BUY", intent.Side);
    }

    [Fact]
    public async Task SubmitSignalAsync_SkipsResubmissionIfAlreadySubmitted()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: null, RiskTier: "FILTERS"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

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
                CurrentPrice: 150.5m));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_789",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var clientOrderId1 = await orderManager.SubmitSignalAsync(signal, 100, 150m);
        var clientOrderId2 = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        Assert.Equal(clientOrderId1, clientOrderId2);
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitSignalAsync_AutoSizesQuantity_WhenZeroPassedAsQuantity()
    {
        // qty = min(equity_cap, risk_cap)
        // equity_cap = floor(100000 * 0.01 / 150m) = floor(6.67) = 6
        // risk_cap   = floor(100000 * 0.01 / (150m * 0.02)) = floor(33.33) = 33
        // result = min(6, 33) = 6
        const int expectedQty = 6;

        var options = new TradingOptions
        {
            RiskLimits = new RiskLimits { MaxRiskPerTradePct = 0.01m, StopLossPct = 0.02m },
            Execution = new ExecutionOptions { DryRun = false },
        };

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: null, RiskTier: "FILTERS"));

        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2023-06-01T10:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m));

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 50000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_auto",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: expectedQty,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        // Act: pass quantity=0 (sentinel for auto-sizing via PositionSizer)
        await orderManager.SubmitSignalAsync(signal, 0, 150m);

        // Assert: broker received the auto-sized quantity
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<int>(q => q == expectedQty),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExitAsync_SubmitsExitOrder()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_exit",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "SELL",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        await orderManager.SubmitExitAsync("AAPL", "SELL", 100, 145m);

        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }
}
