namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for EventBus (normal channel may drop on full, exit/order updates never drop).
/// </summary>
public sealed class EventBusTests
{
    [Fact]
    public async Task PublishAsync_AcceptsNormalEvent()
    {
        var eventBus = new EventBusService();
        var barEvent = new BarEvent(
            Symbol: "AAPL",
            Timeframe: "1m",
            Timestamp: DateTimeOffset.UtcNow,
            Open: 150m,
            High: 151m,
            Low: 149m,
            Close: 150.5m,
            Volume: 1000);

        var published = await eventBus.PublishAsync(barEvent);

        Assert.True(published);
        Assert.Equal(0, eventBus.DroppedCount);
    }

    [Fact]
    public async Task PublishAsync_AcceptsExitSignalNeverDrops()
    {
        var eventBus = new EventBusService();
        var exitSignal = new ExitSignalEvent(
            Symbol: "AAPL",
            ExitReason: "STOP_LOSS",
            ExitPrice: 145m,
            CreatedAt: DateTimeOffset.UtcNow);

        var published = await eventBus.PublishAsync(exitSignal);

        Assert.True(published);
        Assert.Equal(0, eventBus.DroppedCount);
    }

    [Fact]
    public async Task PublishAsync_DropsNormalEventsWhenFull()
    {
        // Use small capacity to keep test fast and reliable
        const int capacity = 10;
        var eventBus = new EventBusService(normalChannelCapacity: capacity);

        // Fill the bounded channel past capacity
        var published = true;
        for (var i = 0; i < capacity + 1; i++)
        {
            var barEvent = new BarEvent(
                Symbol: $"SYM{i % 100}",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow,
                Open: 100m + i,
                High: 101m + i,
                Low: 99m + i,
                Close: 100.5m + i,
                Volume: 1000);

            published = await eventBus.PublishAsync(barEvent);
        }

        // Last one should be dropped
        Assert.False(published);
        Assert.True(eventBus.DroppedCount > 0);
    }

    [Fact]
    public async Task PublishAsync_OrderUpdateNeverDrops_WhenNormalChannelIsFull()
    {
        const int capacity = 2;
        var eventBus = new EventBusService(normalChannelCapacity: capacity);

        // Fill bounded normal channel.
        for (var i = 0; i < capacity; i++)
        {
            var barEvent = new BarEvent(
                Symbol: $"SYM{i}",
                Timeframe: "1m",
                Timestamp: DateTimeOffset.UtcNow,
                Open: 100m,
                High: 101m,
                Low: 99m,
                Close: 100.5m,
                Volume: 1000);
            _ = await eventBus.PublishAsync(barEvent);
        }

        var orderUpdate = new OrderUpdateEvent(
            AlpacaOrderId: "alpaca-1",
            ClientOrderId: "client-1",
            Symbol: "AAPL",
            Side: "BUY",
            FilledQuantity: 10m,
            RemainingQuantity: 0m,
            AverageFilledPrice: 100m,
            Status: OrderState.Filled,
            UpdatedAt: DateTimeOffset.UtcNow);

        var published = await eventBus.PublishAsync(orderUpdate);
        Assert.True(published);

        var processedOrderUpdates = new List<OrderUpdateEvent>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await eventBus.DispatchAsync(
                async @event =>
                {
                    if (@event is OrderUpdateEvent e)
                        processedOrderUpdates.Add(e);
                    await ValueTask.CompletedTask;
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        Assert.Single(processedOrderUpdates);
        Assert.Equal("client-1", processedOrderUpdates[0].ClientOrderId);
    }

    [Fact]
    public async Task DispatchAsync_ProcessesAllEvents()
    {
        var eventBus = new EventBusService();
        var processedEvents = new List<IEvent>();

        var barEvent = new BarEvent("AAPL", "1m", DateTimeOffset.UtcNow, 150m, 151m, 149m, 150.5m, 1000);
        var exitSignal = new ExitSignalEvent("AAPL", "STOP_LOSS", 145m, DateTimeOffset.UtcNow);

        await eventBus.PublishAsync(barEvent);
        await eventBus.PublishAsync(exitSignal);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            await eventBus.DispatchAsync(
                async @event =>
                {
                    processedEvents.Add(@event);
                    await ValueTask.CompletedTask;
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Expected timeout
        }

        Assert.NotEmpty(processedEvents);
    }

    /// <summary>
    /// Regression test for the dispatch loop bug where awaiting the wrong task
    /// would block event processing. This test exercises the race condition by
    /// starting DispatchAsync first (so it's blocked in WaitToReadAsync), then
    /// publishing events while it's waiting.
    /// </summary>
    [Fact]
    public async Task DispatchAsync_ProcessesNormalEventsWhenBothChannelsActive()
    {
        var eventBus = new EventBusService();
        var processedBarEvents = new List<BarEvent>();
        var processedExitEvents = new List<ExitSignalEvent>();
        var dispatchEnteredWait = new TaskCompletionSource();
        var dispatchCanProceed = new TaskCompletionSource();

        // Handler that signals when first event is received
        var handler = async (IEvent @event) =>
        {
            switch (@event)
            {
                case BarEvent bar:
                    processedBarEvents.Add(bar);
                    break;
                case ExitSignalEvent exit:
                    processedExitEvents.Add(exit);
                    break;
            }
            await ValueTask.CompletedTask;
        };

        // Start DispatchAsync on background task - it will block waiting for events
        var dispatchTask = Task.Run(async () =>
        {
            // This handler wrapper signals that DispatchAsync has entered the wait loop
            Func<IEvent, ValueTask> wrappedHandler = async (IEvent @event) =>
            {
                // Signal that we're about to handle an event (DispatchAsync got one)
                dispatchEnteredWait.TrySetResult();
                await handler(@event);
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await eventBus.DispatchAsync(wrappedHandler, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected - we cancel after timeout
            }
        });

        // Wait a bit to ensure DispatchAsync has drained initial queues and entered WaitToReadAsync
        await Task.Delay(100);

        // Now publish events while DispatchAsync is waiting in the WhenAny loop
        // This exercises the code path that had the "await normalTask" bug
        var barEvent = new BarEvent(
            Symbol: "BTC/USD",
            Timeframe: "1m",
            Timestamp: DateTimeOffset.UtcNow,
            Open: 85000m,
            High: 85100m,
            Low: 84900m,
            Close: 85050m,
            Volume: 1000);
        await eventBus.PublishAsync(barEvent);

        // Also publish an exit signal at roughly the same time
        var exitSignal = new ExitSignalEvent("AAPL", "STOP_LOSS", 145m, DateTimeOffset.UtcNow);
        await eventBus.PublishAsync(exitSignal);

        // Wait for DispatchAsync to process the events (with timeout)
        var completed = await Task.WhenAny(dispatchTask, Task.Delay(TimeSpan.FromSeconds(3)));
        
        // Assert events were processed
        Assert.Single(processedBarEvents);
        Assert.Single(processedExitEvents);
        Assert.Equal("BTC/USD", processedBarEvents[0].Symbol);
    }
}
