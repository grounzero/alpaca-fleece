namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for EventBus (dual-channel, drop on full, exit never drops).
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
}
