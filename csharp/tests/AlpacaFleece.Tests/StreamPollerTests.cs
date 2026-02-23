namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for StreamPollerService (batching, bar polling, market hours, backoff, order update polling).
/// </summary>
public sealed class StreamPollerTests
{
    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static StreamPollerService MakeService(
        IMarketDataClient? marketDataClient = null,
        IBrokerService? brokerService = null,
        IStateRepository? stateRepository = null,
        IEventBus? eventBus = null,
        IOptions<TradingOptions>? options = null,
        ILogger<StreamPollerService>? logger = null,
        List<string>? symbols = null)
    {
        var opts = options ?? BuildOptions(symbols ?? []);
        return new StreamPollerService(
            marketDataClient ?? Substitute.For<IMarketDataClient>(),
            brokerService ?? Substitute.For<IBrokerService>(),
            stateRepository ?? Substitute.For<IStateRepository>(),
            eventBus ?? Substitute.For<IEventBus>(),
            opts,
            logger ?? Substitute.For<ILogger<StreamPollerService>>());
    }

    private static IOptions<TradingOptions> BuildOptions(List<string> symbols)
    {
        var opts = Substitute.For<IOptions<TradingOptions>>();
        opts.Value.Returns(new TradingOptions
        {
            Symbols = new SymbolsOptions { Symbols = symbols }
        });
        return opts;
    }

    private static ClockInfo ClosedClock() => new(
        IsOpen: false,
        NextOpen: DateTimeOffset.UtcNow.AddHours(12),
        NextClose: DateTimeOffset.UtcNow.AddHours(16),
        FetchedAt: DateTimeOffset.UtcNow);

    private static ClockInfo OpenClock() => new(
        IsOpen: true,
        NextOpen: DateTimeOffset.UtcNow.AddDays(1),
        NextClose: DateTimeOffset.UtcNow.AddHours(7),
        FetchedAt: DateTimeOffset.UtcNow);

    // ------------------------------------------------------------------
    // Existing smoke tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task StreamPollerService_StartsSuccessfully()
    {
        var service = MakeService(symbols: ["AAPL"]);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await service.StartAsync(cts.Token); }
        catch (OperationCanceledException) { }

        Assert.NotNull(service);
    }

    [Fact]
    public async Task StreamPollerService_StopsGracefully()
    {
        var service = MakeService();

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await service.StartAsync(cts.Token); }
        catch (OperationCanceledException) { }

        Assert.NotNull(service);
    }

    [Fact]
    public async Task StreamPollerService_SkipsPolling_WhenMarketClosed()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var marketDataClient = Substitute.For<IMarketDataClient>();

        brokerService.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ClockInfo>(ClosedClock()));

        // Return empty intents so the order loop is a no-op too
        var stateRepository = Substitute.For<IStateRepository>();
        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto>().AsReadOnly()));

        var service = MakeService(
            marketDataClient: marketDataClient,
            brokerService: brokerService,
            stateRepository: stateRepository,
            symbols: ["AAPL"]);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try { await service.StartAsync(cts.Token); }
        catch (OperationCanceledException) { }

        // GetBarsAsync must not have been called — market was closed
        await marketDataClient.DidNotReceive().GetBarsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamPollerService_PollsCryptoSymbols_WhenMarketClosed()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var marketDataClient = Substitute.For<IMarketDataClient>();

        // Market is closed
        brokerService.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ClockInfo>(ClosedClock()));

        // Signal when BTC/USD is actually polled by the background task
        var cryptoPolledTcs = new TaskCompletionSource();
        marketDataClient.GetBarsAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                if (callInfo.ArgAt<string>(0) == "BTC/USD")
                    cryptoPolledTcs.TrySetResult();
                return new ValueTask<IReadOnlyList<Quote>>(new List<Quote>().AsReadOnly());
            });

        var stateRepository = Substitute.For<IStateRepository>();
        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto>().AsReadOnly()));

        // Two symbols: one equity (AAPL), one crypto (BTC/USD)
        var opts = Options.Create(new TradingOptions
        {
            Symbols = new SymbolsOptions
            {
                Symbols = new List<string> { "AAPL", "BTC/USD" },
                CryptoSymbols = new List<string> { "BTC/USD" }
            }
        });

        var service = new StreamPollerService(
            marketDataClient,
            brokerService,
            stateRepository,
            Substitute.For<IEventBus>(),
            opts,
            Substitute.For<ILogger<StreamPollerService>>());

        // Start with a generous timeout; cancel only after we've confirmed crypto was polled
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);

        // Block until the background task actually reaches GetBarsAsync("BTC/USD")
        await cryptoPolledTcs.Task.WaitAsync(TimeSpan.FromSeconds(4));

        // Stop the service now that we have our signal
        await cts.CancelAsync();

        // BTC/USD must have been polled (crypto — exempt from market hours)
        await marketDataClient.Received().GetBarsAsync(
            "BTC/USD", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        // AAPL must NOT have been polled when market is closed (equity)
        await marketDataClient.DidNotReceive().GetBarsAsync(
            "AAPL", Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StreamPollerService_RetryOnError()
    {
        var brokerService = Substitute.For<IBrokerService>();
        brokerService.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ClockInfo>(OpenClock()));

        var stateRepository = Substitute.For<IStateRepository>();
        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto>().AsReadOnly()));

        var service = MakeService(brokerService: brokerService, stateRepository: stateRepository);

        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try { await service.StartAsync(cts.Token); }
        catch (OperationCanceledException) { }

        Assert.NotNull(service);
    }

    // ------------------------------------------------------------------
    // Batch helper tests
    // ------------------------------------------------------------------

    [Fact]
    public void EnumerableExtensions_Batch_PartitionsCorrectly()
    {
        var batches = new[] { 1, 2, 3, 4, 5, 6, 7 }.Batch(3).ToList();

        Assert.Equal(3, batches.Count);
        Assert.Equal(3, batches[0].Count());
        Assert.Equal(3, batches[1].Count());
        Assert.Equal(1, batches[2].Count());
    }

    [Fact]
    public void EnumerableExtensions_Batch_HandlesExactDivision()
    {
        var batches = new[] { 1, 2, 3, 4, 5, 6 }.Batch(3).ToList();

        Assert.Equal(2, batches.Count);
        Assert.All(batches, b => Assert.Equal(3, b.Count()));
    }

    [Fact]
    public void EnumerableExtensions_Batch_HandlesSingleElement()
    {
        var batches = new[] { 1 }.Batch(5).ToList();

        Assert.Single(batches);
        Assert.Single(batches[0]);
    }

    [Fact]
    public void EnumerableExtensions_Batch_HandlesEmptyEnumerable()
    {
        var batches = Array.Empty<int>().Batch(3).ToList();

        Assert.Empty(batches);
    }

    // ------------------------------------------------------------------
    // BarsHandler smoke tests
    // ------------------------------------------------------------------

    [Fact]
    public void BarsHandler_InitializesWithEmptyDeques()
    {
        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            Substitute.For<IDbContextFactory<TradingDbContext>>(),
            Substitute.For<ILogger<BarsHandler>>());

        Assert.NotNull(handler);
    }

    [Fact]
    public void BarsHandler_GetBarsForSymbol_ReturnsEmpty_WhenNotFound()
    {
        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            Substitute.For<IDbContextFactory<TradingDbContext>>(),
            Substitute.For<ILogger<BarsHandler>>());

        Assert.Empty(handler.GetBarsForSymbol("NONEXISTENT"));
    }

    [Fact]
    public void BarsHandler_GetBarCount_ReturnsZero_WhenNotFound()
    {
        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            Substitute.For<IDbContextFactory<TradingDbContext>>(),
            Substitute.For<ILogger<BarsHandler>>());

        Assert.Equal(0, handler.GetBarCount("NONEXISTENT"));
    }

    [Fact]
    public void BarsHandler_Clear_RemovesAllDeques()
    {
        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            Substitute.For<IDbContextFactory<TradingDbContext>>(),
            Substitute.For<ILogger<BarsHandler>>());

        handler.Clear();

        Assert.Equal(0, handler.GetBarCount("AAPL"));
    }

    // ------------------------------------------------------------------
    // Order update polling tests
    // Call PollOrderUpdatesAsync directly (internal) rather than waiting for the
    // 2-second background loop cadence, keeping tests fast and deterministic.
    // ------------------------------------------------------------------

    [Fact]
    public async Task OrderPollLoop_PublishesOrderUpdateEvent_WhenStatusChangesToFilled()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();
        var eventBus = Substitute.For<IEventBus>();

        // DB has one pending order
        var intent = new OrderIntentDto(
            ClientOrderId: "client-1",
            AlpacaOrderId: "alpaca-1",
            Symbol: "AAPL",
            Side: "buy",
            Quantity: 10,
            LimitPrice: 150m,
            Status: OrderState.PendingNew,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto> { intent }.AsReadOnly()));

        // Alpaca returns the order as Filled
        var filledOrder = new OrderInfo(
            AlpacaOrderId: "alpaca-1",
            ClientOrderId: "client-1",
            Symbol: "AAPL",
            Side: "buy",
            Quantity: 10,
            FilledQuantity: 10,
            AverageFilledPrice: 150.25m,
            Status: OrderState.Filled,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow);

        brokerService.GetOrderByIdAsync("alpaca-1", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<OrderInfo?>(filledOrder));

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository,
            eventBus: eventBus);

        // Call the polling method directly (internal) — no timer race
        await service.PollOrderUpdatesAsync(CancellationToken.None);

        // OrderUpdateEvent must have been published with fill details
        await eventBus.Received(1).PublishAsync(
            Arg.Is<OrderUpdateEvent>(e =>
                e.ClientOrderId == "client-1" &&
                e.Status == OrderState.Filled &&
                e.FilledQuantity == 10),
            Arg.Any<CancellationToken>());

        // DB status must have been updated
        await stateRepository.Received(1).UpdateOrderIntentAsync(
            "client-1",
            "alpaca-1",
            OrderState.Filled,
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());

        // Fill must have been persisted (idempotent)
        await stateRepository.Received(1).InsertFillIdempotentAsync(
            "alpaca-1",
            "client-1",
            10,
            150.25m,
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPollLoop_SkipsOrders_WithNullAlpacaOrderId()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();
        var eventBus = Substitute.For<IEventBus>();

        // Order intent with no AlpacaOrderId yet (not yet submitted to Alpaca)
        var intent = new OrderIntentDto(
            ClientOrderId: "client-2",
            AlpacaOrderId: null,
            Symbol: "MSFT",
            Side: "buy",
            Quantity: 5,
            LimitPrice: 300m,
            Status: OrderState.PendingNew,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto> { intent }.AsReadOnly()));

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository,
            eventBus: eventBus);

        await service.PollOrderUpdatesAsync(CancellationToken.None);

        // GetOrderByIdAsync must not have been called — no Alpaca ID to query
        await brokerService.DidNotReceive()
            .GetOrderByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());

        await eventBus.DidNotReceive()
            .PublishAsync(Arg.Any<OrderUpdateEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPollLoop_SkipsTerminalOrders()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();

        // Only terminal orders in DB
        var filledIntent = new OrderIntentDto(
            "client-3", "alpaca-3", "TSLA", "sell", 2, 800m,
            OrderState.Filled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var cancelledIntent = new OrderIntentDto(
            "client-4", "alpaca-4", "TSLA", "buy", 3, 790m,
            OrderState.Canceled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto> { filledIntent, cancelledIntent }.AsReadOnly()));

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository);

        await service.PollOrderUpdatesAsync(CancellationToken.None);

        // Terminal orders must not be queried
        await brokerService.DidNotReceive()
            .GetOrderByIdAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPollLoop_DoesNotPublish_WhenStatusUnchanged()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();
        var eventBus = Substitute.For<IEventBus>();

        var intent = new OrderIntentDto(
            "client-5", "alpaca-5", "AMZN", "buy", 1, 200m,
            OrderState.Accepted, DateTimeOffset.UtcNow, null);

        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto> { intent }.AsReadOnly()));

        // Alpaca still shows same status: Accepted
        var sameOrder = new OrderInfo(
            "alpaca-5", "client-5", "AMZN", "buy", 1, 0, 0m,
            OrderState.Accepted, DateTimeOffset.UtcNow, null);

        brokerService.GetOrderByIdAsync("alpaca-5", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<OrderInfo?>(sameOrder));

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository,
            eventBus: eventBus);

        await service.PollOrderUpdatesAsync(CancellationToken.None);

        // No event when status is unchanged
        await eventBus.DidNotReceive()
            .PublishAsync(Arg.Any<OrderUpdateEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPollLoop_HandlesNotFoundOrder_Gracefully()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();
        var eventBus = Substitute.For<IEventBus>();

        var intent = new OrderIntentDto(
            "client-6", "alpaca-6", "GOOG", "buy", 1, 150m,
            OrderState.PendingNew, DateTimeOffset.UtcNow, null);

        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto> { intent }.AsReadOnly()));

        // Alpaca returns null (order not found)
        brokerService.GetOrderByIdAsync("alpaca-6", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<OrderInfo?>((OrderInfo?)null));

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository,
            eventBus: eventBus);

        // Should not throw
        await service.PollOrderUpdatesAsync(CancellationToken.None);

        await eventBus.DidNotReceive()
            .PublishAsync(Arg.Any<OrderUpdateEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPollLoop_EmitsPartialFillEvent_WhenStatusChangesToPartiallyFilled()
    {
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();
        var eventBus = Substitute.For<IEventBus>();

        var intent = new OrderIntentDto(
            "client-7", "alpaca-7", "NVDA", "buy", 10, 800m,
            OrderState.PendingNew, DateTimeOffset.UtcNow, null);

        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto> { intent }.AsReadOnly()));

        // Alpaca returns partially filled
        var partialOrder = new OrderInfo(
            "alpaca-7", "client-7", "NVDA", "buy", 10, 5, 800.10m,
            OrderState.PartiallyFilled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        brokerService.GetOrderByIdAsync("alpaca-7", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<OrderInfo?>(partialOrder));

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository,
            eventBus: eventBus);

        await service.PollOrderUpdatesAsync(CancellationToken.None);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<OrderUpdateEvent>(e =>
                e.ClientOrderId == "client-7" &&
                e.Status == OrderState.PartiallyFilled &&
                e.FilledQuantity == 5 &&
                e.RemainingQuantity == 5),
            Arg.Any<CancellationToken>());

        // Partial fill should also be persisted
        await stateRepository.Received(1).InsertFillIdempotentAsync(
            "alpaca-7",
            "client-7",
            5,
            800.10m,
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }
}
