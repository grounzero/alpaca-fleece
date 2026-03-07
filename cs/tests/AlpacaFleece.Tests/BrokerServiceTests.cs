using Alpaca.Markets;

namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for AlpacaBrokerService (clock never cached, cache on reads, no retry on writes).
/// </summary>
public sealed class BrokerServiceTests
{
    private static (AlpacaBrokerService Broker, IAlpacaTradingClient MockClient) CreateBroker(
        BrokerOptions? options = null)
    {
        options ??= new BrokerOptions { ApiKey = "test", SecretKey = "test" };
        var mockClient = Substitute.For<IAlpacaTradingClient>();
        var logger = Substitute.For<ILogger<AlpacaBrokerService>>();

        var mockClock = Substitute.For<IClock>();
        mockClock.IsOpen.Returns(true);
        mockClock.NextOpenUtc.Returns(DateTime.UtcNow.AddDays(1));
        mockClock.NextCloseUtc.Returns(DateTime.UtcNow.AddHours(7));
        mockClient.GetClockAsync(Arg.Any<CancellationToken>()).Returns(mockClock);

        var mockAccount = Substitute.For<IAccount>();
        mockAccount.AccountId.Returns(Guid.NewGuid());
        mockAccount.TradableCash.Returns(100000m);
        mockAccount.Equity.Returns((decimal?)100000m);
        mockAccount.DayTradeCount.Returns(0ul);
        mockAccount.IsTradingBlocked.Returns(false);
        mockAccount.IsAccountBlocked.Returns(false);
        mockClient.GetAccountAsync(Arg.Any<CancellationToken>()).Returns(mockAccount);

        mockClient.ListPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<IPosition>().AsReadOnly());

        return (new AlpacaBrokerService(options, mockClient, logger), mockClient);
    }

    [Fact]
    public async Task GetClockAsync_NeverCaches()
    {
        var (broker, mockClient) = CreateBroker();

        await broker.GetClockAsync();
        await broker.GetClockAsync();

        // Verify the client was called twice (no caching)
        await mockClient.Received(2).GetClockAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetAccountAsync_CachesTtl()
    {
        var (broker, _) = CreateBroker();

        var account1 = await broker.GetAccountAsync();
        var account2 = await broker.GetAccountAsync();

        // Both should have same FetchedAt (cached within 1s)
        Assert.Equal(account1.FetchedAt, account2.FetchedAt);
    }

    [Fact]
    public async Task GetPositionsAsync_CachesTtl()
    {
        var (broker, _) = CreateBroker();

        var positions1 = await broker.GetPositionsAsync();
        var positions2 = await broker.GetPositionsAsync();

        // Should return same instance (cached)
        Assert.Same(positions1, positions2);
    }

    [Fact]
    public async Task SubmitOrderAsync_ThrowsWhenKillSwitchActive()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test",
            KillSwitch = true
        };
        var (broker, _) = CreateBroker(options);

        var ex = await Assert.ThrowsAsync<BrokerFatalException>(
            () => broker.SubmitOrderAsync("AAPL", "BUY", 100, 150m, "order_123").AsTask());

        Assert.Contains("Kill switch", ex.Message);
    }

    [Fact]
    public async Task SubmitOrderAsync_DryRunDoesNotSubmit()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test",
            DryRun = true
        };
        var (broker, _) = CreateBroker(options);

        var order = await broker.SubmitOrderAsync("AAPL", "BUY", 100, 150m, "order_123");

        Assert.Equal(OrderState.Accepted, order.Status);
        Assert.StartsWith("dry-", order.AlpacaOrderId);
    }

    [Fact]
    public async Task SubmitOrderAsync_InvalidatesPositionsCache()
    {
        // Feature 3: after a successful order, the positions cache must be invalidated
        // so the next GetPositionsAsync call fetches fresh data from the broker.
        var (broker, mockClient) = CreateBroker();

        // Configure PostOrderAsync with a minimal IOrder mock
        var mockOrder = Substitute.For<IOrder>();
        mockOrder.OrderId.Returns(Guid.NewGuid());
        mockOrder.Symbol.Returns("AAPL");
        mockOrder.OrderSide.Returns(OrderSide.Buy);
        mockOrder.IntegerQuantity.Returns(100L);
        mockOrder.IntegerFilledQuantity.Returns(0L);
        mockOrder.AverageFillPrice.Returns((decimal?)null);
        mockOrder.OrderStatus.Returns(OrderStatus.Accepted);
        mockOrder.CreatedAtUtc.Returns((DateTime?)null);
        mockOrder.UpdatedAtUtc.Returns((DateTime?)null);

        mockClient.PostOrderAsync(
            Arg.Any<NewOrderRequest>(), Arg.Any<CancellationToken>())
            .Returns(mockOrder);

        // Prime the positions cache (first fetch)
        await broker.GetPositionsAsync();

        // Submit a real (non-DryRun) order — this must invalidate _positionsCacheTime
        await broker.SubmitOrderAsync("AAPL", "BUY", 100, 150m, "client-id");

        // Second fetch: cache was invalidated, so ListPositionsAsync is called again
        await broker.GetPositionsAsync();

        // ListPositionsAsync must have been called exactly twice (once before, once after order)
        await mockClient.Received(2).ListPositionsAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CancelOrderAsync_DryRunSkipsCall()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test",
            DryRun = true
        };
        var (broker, mockClient) = CreateBroker(options);

        // Dry run should complete without calling the SDK
        await broker.CancelOrderAsync(Guid.NewGuid().ToString());

        await mockClient.DidNotReceive().CancelOrderAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsFractionalFault_FilledAndQtyZero_ReturnsTrue()
    {
        var info = new OrderInfo(
            AlpacaOrderId: "a", ClientOrderId: "c", Symbol: "BTC/USD",
            Side: "buy", Quantity: 0m, FilledQuantity: 0m,
            AverageFilledPrice: 0m, Status: OrderState.Filled,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: null);

        Assert.True(AlpacaBrokerService.IsFractionalFault(info));
    }

    [Fact]
    public void IsFractionalFault_FilledAndQtyNonZero_ReturnsFalse()
    {
        var info = new OrderInfo(
            AlpacaOrderId: "a", ClientOrderId: "c", Symbol: "AAPL",
            Side: "buy", Quantity: 10m, FilledQuantity: 10m,
            AverageFilledPrice: 150m, Status: OrderState.Filled,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: null);

        Assert.False(AlpacaBrokerService.IsFractionalFault(info));
    }

    [Fact]
    public void IsFractionalFault_PartiallyFilledAndQtyZero_ReturnsTrue()
    {
        var info = new OrderInfo(
            AlpacaOrderId: "a", ClientOrderId: "c", Symbol: "ETH/USD",
            Side: "buy", Quantity: 0m, FilledQuantity: 0m,
            AverageFilledPrice: 0m, Status: OrderState.PartiallyFilled,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: null);

        Assert.True(AlpacaBrokerService.IsFractionalFault(info));
    }

    [Fact]
    public void IsFractionalFault_AcceptedAndQtyZero_ReturnsFalse()
    {
        // Only Filled/PartiallyFilled statuses trigger the fault; Accepted is not terminal-fill
        var info = new OrderInfo(
            AlpacaOrderId: "a", ClientOrderId: "c", Symbol: "BTC/USD",
            Side: "buy", Quantity: 0m, FilledQuantity: 0m,
            AverageFilledPrice: 0m, Status: OrderState.Accepted,
            CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: null);

        Assert.False(AlpacaBrokerService.IsFractionalFault(info));
    }

    [Fact]
    public async Task GetOrderByIdAsync_ReturnsNull_OnInvalidGuid()
    {
        var (broker, _) = CreateBroker();

        var result = await broker.GetOrderByIdAsync("not-a-guid");

        Assert.Null(result);
    }
}
