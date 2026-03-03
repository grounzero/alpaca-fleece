namespace AlpacaFleece.Tests;

/// <summary>
/// End-to-end Phase 3 integration tests.
/// Verifies complete flow: Signal → RiskManager → OrderManager → Database.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class Phase3IntegrationTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly ILogger<RiskManager> _riskManagerLogger = Substitute.For<ILogger<RiskManager>>();
    private readonly ILogger<OrderManager> _orderManagerLogger = Substitute.For<ILogger<OrderManager>>();
    private readonly TradingOptions _options = new()
    {
        Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
        Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
    };

    public async Task InitializeAsync()
    {
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "0");
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");
        await fixture.StateRepository.SaveCircuitBreakerCountAsync(0);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SignalFlow_BarArrives_SignalGenerated_RiskCheckPasses_OrderSubmitted()
    {
        // Setup broker mock to approve orders and market conditions
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
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
                AlpacaOrderId: "alpaca_123",
                ClientOrderId: "client_123",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 50,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        // Create managers
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, _options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            _options,
            _orderManagerLogger);

        // Create signal
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
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        // Execute flow
        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Verify database state
        var intent = await fixture.StateRepository.GetOrderIntentAsync(clientOrderId);
        Assert.NotNull(intent);
        Assert.Equal("AAPL", intent.Symbol);
        Assert.Equal("BUY", intent.Side);
        Assert.Equal(50, intent.Quantity);
        Assert.NotNull(intent.AlpacaOrderId);
    }

    [Fact]
    public async Task DuplicateSignal_SubmittedTwice_SecondCallReturnsSameId()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
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
                AlpacaOrderId: "alpaca_456",
                ClientOrderId: "client_456",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 50,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, _options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            _options,
            _orderManagerLogger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2024-02-21T14:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        // Submit first time
        var id1 = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Submit identical signal again
        var id2 = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Should return same ID (idempotent)
        Assert.Equal(id1, id2);

        // Broker should only be called once
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CircuitBreakerTrip_FiveConsecutiveFailures_BlocksSubsequentSignals()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        // Broker throws to simulate failures
        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<OrderInfo>(new Exception("Broker connection failed")));

        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, _options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            _options,
            _orderManagerLogger);

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
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        // Simulate 5 failures
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await orderManager.SubmitSignalAsync(signal, 50, 150.5m);
            }
            catch (OrderManagerException)
            {
                // Expected
            }
        }

        // Check circuit breaker is tripped
        var count = await fixture.StateRepository.GetCircuitBreakerCountAsync();
        Assert.Equal(5, count);

        // Next signal should fail due to circuit breaker
        var ex = await Assert.ThrowsAsync<RiskManagerException>(
            async () => await orderManager.SubmitSignalAsync(signal, 50, 150.5m));

        Assert.Contains("Circuit breaker", ex.Message);
    }

    [Fact]
    public async Task DryRunMode_OrderIntentPersisted_BrokerNotCalled()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        var dryRunOptions = new TradingOptions
        {
            Execution = new ExecutionOptions { DryRun = true },
            Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
            Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
        };
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, dryRunOptions, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            dryRunOptions,
            _orderManagerLogger);

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
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Intent should be persisted
        var intent = await fixture.StateRepository.GetOrderIntentAsync(clientOrderId);
        Assert.NotNull(intent);

        // Broker should NOT be called
        await _brokerMock.DidNotReceive().SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderIdGenerator_ProducesDeterministicIds()
    {
        var strategy = "sma_crossover_multi";
        var symbol = "AAPL";
        var timeframe = "1Min";
        var timestamp = DateTimeOffset.Parse("2024-02-21T14:30:00Z");
        var side = "buy";

        var id1 = OrderIdGenerator.GenerateClientOrderId(strategy, symbol, timeframe, timestamp, side);
        var id2 = OrderIdGenerator.GenerateClientOrderId(strategy, symbol, timeframe, timestamp, side);

        // Same inputs should produce same ID
        Assert.Equal(id1, id2);

        // ID should be exactly 16 hex characters
        Assert.Equal(16, id1.Length);
        Assert.True(id1.All(c => "0123456789abcdef".Contains(c)));

        // Different side should produce different ID
        var idSell = OrderIdGenerator.GenerateClientOrderId(strategy, symbol, timeframe, timestamp, "sell");
        Assert.NotEqual(id1, idSell);
    }
}
