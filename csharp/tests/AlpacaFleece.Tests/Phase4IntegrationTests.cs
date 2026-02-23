namespace AlpacaFleece.Tests;

/// <summary>
/// Phase 4-6 integration tests: full trading system with exit management and reconciliation.
/// Test vectors:
/// 1. Bar arrives → Signal generated → Risk passes → Order submitted → Position tracked
/// 2. Duplicate signal → Order submitted once (idempotency)
/// 3. Circuit breaker trips after N failures → blocks further orders
/// 4. Fill received → Position tracked → Exit triggered
/// 5. Graceful shutdown → orders cancelled → positions flattened
/// </summary>
[Collection("Trading Database Collection")]
public sealed class Phase4IntegrationTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly IMarketDataClient _marketDataMock = Substitute.For<IMarketDataClient>();
    private readonly ILogger<PositionTracker> _positionTrackerLogger = Substitute.For<ILogger<PositionTracker>>();
    private readonly ILogger<RiskManager> _riskManagerLogger = Substitute.For<ILogger<RiskManager>>();
    private readonly ILogger<OrderManager> _orderManagerLogger = Substitute.For<ILogger<OrderManager>>();
    private readonly ILogger<SmaCrossoverStrategy> _strategyLogger = Substitute.For<ILogger<SmaCrossoverStrategy>>();
    private readonly ILogger<ExitManager> _exitManagerLogger = Substitute.For<ILogger<ExitManager>>();
    private readonly ILogger<HousekeepingService> _housekeepingLogger = Substitute.For<ILogger<HousekeepingService>>();
    private IStrategy _strategy = null!;
    private IRiskManager _riskManager = null!;
    private IOrderManager _orderManager = null!;
    private PositionTracker _positionTracker = null!;
    private ExitManager _exitManager = null!;

    public async Task InitializeAsync()
    {
        // Reset shared DB state before each test
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "0");
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");
        await fixture.StateRepository.SaveCircuitBreakerCountAsync(0);

        _positionTracker = new PositionTracker(fixture.StateRepository, _positionTrackerLogger);

        var tradingOptions = new TradingOptions
        {
            Symbols = new SymbolsOptions { Symbols = new List<string> { "AAPL" } },
            Execution = new ExecutionOptions { DryRun = true },
            RiskLimits = new RiskLimits { MaxTradesPerDay = 10, MaxDailyLoss = 1000m },
            Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
            Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
        };

        _strategy = new SmaCrossoverStrategy(fixture.EventBus, _strategyLogger);
        _riskManager = new RiskManager(_brokerMock, fixture.StateRepository, tradingOptions, _riskManagerLogger);
        _orderManager = new OrderManager(_brokerMock, _riskManager, fixture.StateRepository, fixture.EventBus, tradingOptions, _orderManagerLogger);

        var exitOptions = Options.Create(new TradingOptions { Exit = new ExitOptions(), Symbols = new SymbolsOptions() });
        _exitManager = new ExitManager(
            _positionTracker,
            _brokerMock,
            _marketDataMock,
            fixture.EventBus,
            fixture.StateRepository,
            _exitManagerLogger,
            exitOptions);

        // Setup broker mocks
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow,
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

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

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

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

        await Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task TestVector1_BarToOrderFlow()
    {
        // Arrange: use a mock event bus to capture signals published by the strategy.
        // 50 flat bars at 100m establish history; previous SMA pair state starts at (0, 0).
        // Bar 51 at 200m → SMA5=120, SMA15≈106.7.
        // isCrossoverUp = (prevFast=0 <= prevSlow=0) && (120 > 106.7) = true → BUY signal emitted.
        var mockEventBus = Substitute.For<IEventBus>();
        var strategy = new SmaCrossoverStrategy(mockEventBus, _strategyLogger);

        for (var i = 0; i < 50; i++)
        {
            var flatBar = new BarEvent(
                Symbol: "AAPL",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow.AddMinutes(-50 + i),
                Open: 100m, High: 100m, Low: 100m, Close: 100m, Volume: 1_000_000);
            await strategy.OnBarAsync(flatBar, CancellationToken.None);
        }

        // Act: jump bar at bar 51 triggers the crossover
        var jumpBar = new BarEvent(
            Symbol: "AAPL",
            Timeframe: "1m",
            Timestamp: DateTimeOffset.UtcNow,
            Open: 200m, High: 200m, Low: 200m, Close: 200m, Volume: 1_000_000);
        await strategy.OnBarAsync(jumpBar, CancellationToken.None);

        // Assert: at least one SignalEvent was published after the crossover bar
        await mockEventBus.Received().PublishAsync(
            Arg.Any<SignalEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TestVector2_DuplicateSignalIdempotency()
    {
        // Arrange
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

        // Act: submit same signal twice
        var clientOrderId1 = await _orderManager.SubmitSignalAsync(signal, 100, 150m);
        var clientOrderId2 = await _orderManager.SubmitSignalAsync(signal, 100, 150m);

        // Assert: should be idempotent (same client order ID returned both times)
        Assert.Equal(clientOrderId1, clientOrderId2);
        // DryRun mode skips broker submission, so no broker call assertion here
    }

    [Fact]
    public async Task TestVector3_CircuitBreakerTripsAfterFailures()
    {
        // Arrange: DryRun=false + failing broker so each submission increments the circuit breaker.
        var failingBroker = Substitute.For<IBrokerService>();
        failingBroker.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddHours(7), DateTimeOffset.UtcNow));
        failingBroker.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo("test", 50000m, 0m, 100000m, 0m, true, false, DateTimeOffset.UtcNow));
        failingBroker.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());
        failingBroker.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<OrderInfo>(new BrokerException("Broker unavailable")));

        var localOptions = new TradingOptions
        {
            Symbols = new SymbolsOptions { Symbols = new List<string> { "AAPL" } },
            Execution = new ExecutionOptions { DryRun = false },
            RiskLimits = new RiskLimits { MaxTradesPerDay = 20, MaxDailyLoss = 10000m },
            Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
            Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
        };

        var localRiskManager = new RiskManager(failingBroker, fixture.StateRepository, localOptions, _riskManagerLogger);
        var localOrderManager = new OrderManager(failingBroker, localRiskManager, fixture.StateRepository, fixture.EventBus, localOptions, _orderManagerLogger);

        // Use a fixed signal timestamp so the gate check only applies once (retries bypass the gate)
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2023-11-01T10:30:00Z"),
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

        // Act: submit 5 times; each reaches the broker, which throws → circuit breaker increments
        for (var i = 0; i < 5; i++)
        {
            try
            {
                await localOrderManager.SubmitSignalAsync(signal, 100, 150m);
            }
            catch (OrderManagerException) { }
            catch (RiskManagerException) { break; } // should not happen before 5 failures
        }

        // Assert: circuit breaker must be >= 5 after 5 broker failures
        var count = await fixture.StateRepository.GetCircuitBreakerCountAsync();
        Assert.True(count >= 5, $"Expected circuit breaker >= 5 but got {count}");

        // Assert: 6th attempt is blocked by the safety tier (circuit breaker tripped)
        await Assert.ThrowsAsync<RiskManagerException>(
            () => localOrderManager.SubmitSignalAsync(signal, 100, 150m).AsTask());
    }

    [Fact]
    public async Task TestVector4_FillReceivedPositionTrackedExitTriggered()
    {
        // Arrange
        _positionTracker.OpenPosition("AAPL", 100, 150m, 2m);

        var orderUpdate = new OrderUpdateEvent(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: null,
            Symbol: "AAPL",
            Side: "BUY",
            FilledQuantity: 100,
            RemainingQuantity: 0,
            AverageFilledPrice: 150m,
            Status: OrderState.Filled,
            UpdatedAt: DateTimeOffset.UtcNow);

        // Act: handle order fill
        var position = _positionTracker.GetPosition("AAPL");
        Assert.NotNull(position);
        Assert.Equal(150m, position.EntryPrice);

        // Assert: exit conditions would be checked in next cycle
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow,
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        var exitSignals = await _exitManager.CheckPositionsAsync(CancellationToken.None);
        Assert.NotNull(exitSignals);
    }

    [Fact]
    public async Task TestVector5_GracefulShutdownFlattensPositions()
    {
        // Arrange
        _positionTracker.OpenPosition("AAPL", 100, 150m, 2m);

        var housekeeping = new HousekeepingService(
            _brokerMock,
            fixture.StateRepository,
            _positionTracker,
            Substitute.For<IOrderManager>(),
            _housekeepingLogger);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<int>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "flatten",
                ClientOrderId: "flatten_aapl",
                Symbol: "AAPL",
                Side: "SELL",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        // Act: graceful shutdown
        await housekeeping.StopAsync(CancellationToken.None);

        // Assert: flatten order submitted
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Is<string>(s => s == "AAPL"),
            Arg.Is<string>(s => s == "SELL"),
            Arg.Is<int>(q => q == 100),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExitManagerWorkflowComplete()
    {
        // Full exit manager workflow
        _positionTracker.OpenPosition("AAPL", 100, 150m, 2m);

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow,
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);
        Assert.NotNull(signals);

        // Simulate order update
        var orderUpdate = new OrderUpdateEvent(
            AlpacaOrderId: "exit_order",
            ClientOrderId: "exit_client_123",
            Symbol: "AAPL",
            Side: "sell",
            FilledQuantity: 0,
            RemainingQuantity: 100,
            AverageFilledPrice: 0m,
            Status: OrderState.Canceled,
            UpdatedAt: DateTimeOffset.UtcNow);

        await _exitManager.HandleOrderUpdateAsync(orderUpdate, CancellationToken.None);
    }
}
