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
        IStrategy? strategy = null,
        ILogger<StreamPollerService>? logger = null,
        List<string>? symbols = null)
    {
        var opts = options ?? BuildOptions(symbols ?? []);
        var strat = strategy ?? Substitute.For<IStrategy>();
        return new StreamPollerService(
            marketDataClient ?? Substitute.For<IMarketDataClient>(),
            brokerService ?? Substitute.For<IBrokerService>(),
            stateRepository ?? Substitute.For<IStateRepository>(),
            eventBus ?? Substitute.For<IEventBus>(),
            opts,
            strat,
            logger ?? Substitute.For<ILogger<StreamPollerService>>());
    }

    private static IOptions<TradingOptions> BuildOptions(List<string> symbols)
    {
        var opts = Substitute.For<IOptions<TradingOptions>>();
        opts.Value.Returns(new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = symbols }
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

        var opts = Options.Create(new TradingOptions
        {
            Symbols = new SymbolLists
            {
                EquitySymbols = new List<string> { "AAPL", "MSFT", "GOOG" },
                CryptoSymbols = new List<string> { "BTC/USD", "ETH/USD" }
            }
        });

        var service = new StreamPollerService(
            marketDataClient,
            brokerService,
            stateRepository,
            Substitute.For<IEventBus>(),
            opts,
            Substitute.For<IStrategy>(),
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
    public async Task StreamPollerService_ClampsBarDepth_UpToStrategyMinimum()
    {
        // BarHistoryDepth=10 is below RequiredHistory=51; effective depth must be clamped to 51.
        var brokerService = Substitute.For<IBrokerService>();
        var marketDataClient = Substitute.For<IMarketDataClient>();
        var stateRepository = Substitute.For<IStateRepository>();

        brokerService.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ClockInfo>(OpenClock()));
        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto>().AsReadOnly()));

        var capturedLimit = new TaskCompletionSource<int>();
        marketDataClient.GetBarsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedLimit.TrySetResult(callInfo.ArgAt<int>(2));
                return new ValueTask<IReadOnlyList<Quote>>(new List<Quote>().AsReadOnly());
            });

        var opts = Options.Create(new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = new List<string> { "AAPL" } },
            Execution = new ExecutionOptions { BarHistoryDepth = 10 }
        });
        var strategy = Substitute.For<IStrategy>();
        strategy.RequiredHistory.Returns(51);

        var service = new StreamPollerService(
            marketDataClient, brokerService, stateRepository,
            Substitute.For<IEventBus>(), opts, strategy,
            Substitute.For<ILogger<StreamPollerService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        var limit = await capturedLimit.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await cts.CancelAsync();

        Assert.Equal(51, limit);
    }

    [Fact]
    public async Task StreamPollerService_ClampsBarDepth_DownToApiMaximum()
    {
        // BarHistoryDepth=20000 exceeds Alpaca API maximum of 10000; effective depth must be clamped to 10000.
        var brokerService = Substitute.For<IBrokerService>();
        var marketDataClient = Substitute.For<IMarketDataClient>();
        var stateRepository = Substitute.For<IStateRepository>();

        brokerService.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ClockInfo>(OpenClock()));
        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto>().AsReadOnly()));

        var capturedLimit = new TaskCompletionSource<int>();
        marketDataClient.GetBarsAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedLimit.TrySetResult(callInfo.ArgAt<int>(2));
                return new ValueTask<IReadOnlyList<Quote>>(new List<Quote>().AsReadOnly());
            });

        var opts = Options.Create(new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = new List<string> { "AAPL" } },
            Execution = new ExecutionOptions { BarHistoryDepth = 20_000 }
        });
        var strategy = Substitute.For<IStrategy>();
        strategy.RequiredHistory.Returns(51);

        var service = new StreamPollerService(
            marketDataClient, brokerService, stateRepository,
            Substitute.For<IEventBus>(), opts, strategy,
            Substitute.For<ILogger<StreamPollerService>>());

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        var limit = await capturedLimit.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await cts.CancelAsync();

        Assert.Equal(10_000, limit);
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
        Assert.Single(batches[2]);
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
    public void BarsHandler_InitialisesWithEmptyDeques()
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
    public async Task OrderPollLoop_EmitsEvent_WhenQtyIncreasesWithSameStatus()
    {
        // Regression test: status stays PartiallyFilled across two polls but filled qty
        // increases from 5 → 8. The second poll must still emit an OrderUpdateEvent.
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();
        var eventBus = Substitute.For<IEventBus>();

        // First poll returns intent with status PendingNew; second poll returns PartiallyFilled.
        var pollCount = 0;
        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                pollCount++;
                var status = pollCount == 1 ? OrderState.PendingNew : OrderState.PartiallyFilled;
                var intent = new OrderIntentDto(
                    "client-qty", "alpaca-qty", "TSLA", "buy", 10, 200m,
                    status, DateTimeOffset.UtcNow, null);
                return new ValueTask<IReadOnlyList<OrderIntentDto>>(
                    new List<OrderIntentDto> { intent }.AsReadOnly());
            });

        // First broker response: 5 filled (status changes PendingNew → PartiallyFilled).
        // Second broker response: 8 filled (status stays PartiallyFilled, qty increases).
        var brokerCallCount = 0;
        brokerService.GetOrderByIdAsync("alpaca-qty", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                brokerCallCount++;
                var filledQty = brokerCallCount == 1 ? 5m : 8m;
                return new ValueTask<OrderInfo?>(new OrderInfo(
                    "alpaca-qty", "client-qty", "TSLA", "buy", 10, filledQty, 200m,
                    OrderState.PartiallyFilled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
            });

        // Allow UpdateOrderIntentAsync to succeed
        stateRepository.UpdateOrderIntentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OrderState>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository,
            eventBus: eventBus);

        // First poll: status changes PendingNew → PartiallyFilled (filledQty 0→5) → event emitted
        await service.PollOrderUpdatesAsync(CancellationToken.None);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<OrderUpdateEvent>(e => e.FilledQuantity == 5m),
            Arg.Any<CancellationToken>());

        eventBus.ClearReceivedCalls();

        // Second poll: status unchanged (PartiallyFilled), but filledQty 5→8 → event must still fire
        await service.PollOrderUpdatesAsync(CancellationToken.None);

        await eventBus.Received(1).PublishAsync(
            Arg.Is<OrderUpdateEvent>(e => e.FilledQuantity == 8m && e.Status == OrderState.PartiallyFilled),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderPollLoop_NoEvent_WhenStatusAndQtyBothUnchanged()
    {
        // Confirm the guard: status unchanged AND qty unchanged → no event (avoids spurious republish).
        var brokerService = Substitute.For<IBrokerService>();
        var stateRepository = Substitute.For<IStateRepository>();
        var eventBus = Substitute.For<IEventBus>();

        var intent = new OrderIntentDto(
            "client-nodup", "alpaca-nodup", "META", "buy", 10, 300m,
            OrderState.PartiallyFilled, DateTimeOffset.UtcNow, null);

        stateRepository.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto> { intent }.AsReadOnly()));

        // Broker returns same PartiallyFilled status and same filledQty = 5 on both calls
        brokerService.GetOrderByIdAsync("alpaca-nodup", Arg.Any<CancellationToken>())
            .Returns(new ValueTask<OrderInfo?>(new OrderInfo(
                "alpaca-nodup", "client-nodup", "META", "buy", 10, 5m, 300m,
                OrderState.PartiallyFilled, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)));

        stateRepository.UpdateOrderIntentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OrderState>(),
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.CompletedTask);

        var service = MakeService(
            brokerService: brokerService,
            stateRepository: stateRepository,
            eventBus: eventBus);

        // First poll: status changes (PendingNew intent was saved, but here intent is already
        // PartiallyFilled). In this test the intent status equals the broker status AND qty=5
        // equals lastFilledQty=0 (not tracked yet) → first poll emits because status changed
        // from DB-stored PartiallyFilled... Actually, since intent.Status==PartiallyFilled
        // AND order.Status==PartiallyFilled → statusChanged=false, qtyIncreased=(5>0)=true → emits.
        await service.PollOrderUpdatesAsync(CancellationToken.None);
        eventBus.ClearReceivedCalls();

        // Second poll: status unchanged (PartiallyFilled = PartiallyFilled), qty 5 = lastTracked 5
        // → no event
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
