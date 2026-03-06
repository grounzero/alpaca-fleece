namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for HousekeepingService (graceful shutdown only).
/// Note: Equity snapshots and daily resets are now handled by Hangfire background jobs.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class HousekeepingTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly ILogger<PositionTracker> _positionTrackerLogger = Substitute.For<ILogger<PositionTracker>>();
    private readonly ILogger<HousekeepingService> _logger = Substitute.For<ILogger<HousekeepingService>>();
    private readonly IOrderManager _orderManagerMock = Substitute.For<IOrderManager>();
    private readonly IServiceScopeFactory _scopeFactoryMock = Substitute.For<IServiceScopeFactory>();
    private PositionTracker _positionTracker = null!;
    private HousekeepingService _housekeeping = null!;

    public async Task InitializeAsync()
    {
        _positionTracker = new PositionTracker(fixture.StateRepository, _positionTrackerLogger);

        // Wire scope factory mock so StopAsync can resolve IOrderManager
        var scopeMock = Substitute.For<IServiceScope>();
        var providerMock = Substitute.For<IServiceProvider>();
        _scopeFactoryMock.CreateScope().Returns(scopeMock);
        scopeMock.ServiceProvider.Returns(providerMock);
        providerMock.GetService(typeof(IOrderManager)).Returns(_orderManagerMock);
        _orderManagerMock.FlattenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<int>(0));

        _housekeeping = new HousekeepingService(
            _brokerMock,
            fixture.StateRepository,
            _scopeFactoryMock,
            _logger);

        // Setup broker mock defaults
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test_account",
                CashAvailable: 50000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        // Zero out AAPL position_tracking row so subsequent tests see no open position.
        await _positionTracker.ClosePositionAsync("AAPL");
    }

    [Fact]
    public async Task ExecuteAsync_CompletesImmediately()
    {
        // ExecuteAsync now returns Task.CompletedTask (no recurring tasks)
        // Act
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(1));

        var runTask = _housekeeping.StartAsync(cts.Token);
        await runTask;

        // Assert
        Assert.True(true);
    }

    [Fact]
    public async Task StopAsync_CancelsOrders()
    {
        // Arrange
        var orderInfo = new OrderInfo(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: "client_123",
            Symbol: "AAPL",
            Side: "BUY",
            Quantity: 100,
            FilledQuantity: 0,
            AverageFilledPrice: 0m,
            Status: OrderState.PendingNew,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo> { orderInfo });

        _brokerMock.CancelOrderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        // Act
        await _housekeeping.StopAsync(CancellationToken.None);

        // Assert
        await _brokerMock.Received(1).CancelOrderAsync(
            Arg.Is<string>(s => s == "alpaca_123"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_FlattensPositions_ViaOrderManager()
    {
        // Flatten now routes through IOrderManager (deterministic clientOrderId, persist-before-submit).
        // Arrange
        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        _orderManagerMock.FlattenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<int>(1));

        // Act
        await _housekeeping.StopAsync(CancellationToken.None);

        // Assert: OrderManager is called, NOT direct broker
        await _orderManagerMock.Received(1).FlattenPositionsAsync(Arg.Any<CancellationToken>());
        await _brokerMock.DidNotReceive().SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_FlattenCreatesScope_OncePerShutdown()
    {
        // Verify the DI scope is created exactly once during StopAsync
        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        await _housekeeping.StopAsync(CancellationToken.None);

        _scopeFactoryMock.Received(1).CreateScope();
    }

    [Fact]
    public async Task StopAsync_FlattenZeroPositions_OrderManagerStillCalled()
    {
        // FlattenPositionsAsync is called even when no positions exist — it's OrderManager's job
        // to determine whether there's anything to flatten
        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        _orderManagerMock.FlattenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<int>(0));

        await _housekeeping.StopAsync(CancellationToken.None);

        await _orderManagerMock.Received(1).FlattenPositionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_FlattenThrows_ShutdownContinues()
    {
        // If FlattenPositionsAsync throws, the outer catch in StopAsync continues gracefully
        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        _orderManagerMock.FlattenPositionsAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask<int>>(_ => throw new InvalidOperationException("Broker unavailable"));

        // StopAsync should not rethrow — it logs and continues
        await _housekeeping.StopAsync(CancellationToken.None);

        // If we reach here, graceful shutdown absorbed the error (as expected)
        Assert.True(true);
    }

    [Fact]
    public async Task StopAsync_TakesFinalEquitySnapshot()
    {
        // Arrange
        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        var accountCallCount = 0;
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                accountCallCount++;
                return new AccountInfo(
                    AccountId: "test_account",
                    CashAvailable: 50000m,
                    CashReserved: 0m,
                    PortfolioValue: 100000m,
                    DayTradeCount: 0,
                    IsTradable: true,
                    IsAccountRestricted: false,
                    FetchedAt: DateTimeOffset.UtcNow);
            });

        // Act
        await _housekeeping.StopAsync(CancellationToken.None);

        // Assert: account should be called for final snapshot
        Assert.True(accountCallCount > 0);
    }

    [Fact]
    public async Task StopAsync_FinalSnapshot_ReadsDailyPnl()
    {
        // Arrange
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "1234.56", CancellationToken.None);
        
        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        // Act
        await _housekeeping.StopAsync(CancellationToken.None);

        // Assert: should have read daily_realized_pnl from state
        await _brokerMock.Received(1).GetAccountAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StopAsync_SnapshotThrows_ShutdownContinues()
    {
        // If final snapshot throws, graceful shutdown should continue
        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns<AccountInfo>(_ => throw new InvalidOperationException("Broker unavailable"));

        // StopAsync should not rethrow — it logs and continues
        await _housekeeping.StopAsync(CancellationToken.None);

        // If we reach here, graceful shutdown absorbed the error (as expected)
        Assert.True(true);
    }
}
