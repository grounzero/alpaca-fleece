namespace AlpacaFleece.Infrastructure.EventBus;

/// <summary>
/// Event bus implementation with dual-channel architecture.
/// Normal events: bounded channel, FullMode.DropWrite, maxsize=10000.
/// Exit signals: unbounded channel, never dropped.
/// </summary>
public sealed class EventBusService : IEventBus
{
    private readonly Channel<IEvent> _normalChannel;
    private readonly Channel<ExitSignalEvent> _exitChannel;
    private long _droppedCount;

    public long DroppedCount => Volatile.Read(ref _droppedCount);

    public EventBusService(int normalChannelCapacity = 10000)
    {
        var normalOptions = new BoundedChannelOptions(normalChannelCapacity)
        {
            // Use Wait mode so TryWrite returns false (not true) when channel is full
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        };
        _normalChannel = Channel.CreateBounded<IEvent>(normalOptions);

        _exitChannel = Channel.CreateUnbounded<ExitSignalEvent>();
    }

    /// <summary>
    /// Publishes an event to the appropriate channel (no allocations in hot path).
    /// </summary>
    public ValueTask<bool> PublishAsync(IEvent @event, CancellationToken ct = default)
    {
        if (@event is ExitSignalEvent exitSignal)
        {
            return PublishExitSignalAsync(exitSignal, ct);
        }

        return PublishNormalEventAsync(@event, ct);
    }

    private ValueTask<bool> PublishNormalEventAsync(IEvent @event, CancellationToken ct)
    {
        if (_normalChannel.Writer.TryWrite(@event))
        {
            return new ValueTask<bool>(true);
        }

        Volatile.Write(ref _droppedCount, Volatile.Read(ref _droppedCount) + 1);
        return new ValueTask<bool>(false);
    }

    private async ValueTask<bool> PublishExitSignalAsync(ExitSignalEvent exitSignal, CancellationToken ct)
    {
        await _exitChannel.Writer.WriteAsync(exitSignal, ct);
        return true;
    }

    /// <summary>
    /// Dispatches all events from both channels to handler.
    /// Priority: drain exit signals first, then normal events.
    /// </summary>
    public async ValueTask DispatchAsync(Func<IEvent, ValueTask> handler, CancellationToken ct = default)
    {
        // Priority drain: exit signals first
        while (_exitChannel.Reader.TryRead(out var exitSignal))
        {
            await handler(exitSignal);
        }

        // Then normal events
        while (_normalChannel.Reader.TryRead(out var normalEvent))
        {
            await handler(normalEvent);
        }

        // Wait for new events with backoff
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Check for exit signal
                var exitTask = _exitChannel.Reader.WaitToReadAsync(cts.Token).AsTask();
                var normalTask = _normalChannel.Reader.WaitToReadAsync(cts.Token).AsTask();

                var completedTask = await Task.WhenAny(exitTask, normalTask).ConfigureAwait(false);

                if (completedTask == exitTask && await exitTask)
                {
                    while (_exitChannel.Reader.TryRead(out var exitSignal))
                    {
                        await handler(exitSignal);
                    }
                }
                else if (await normalTask)
                {
                    while (_normalChannel.Reader.TryRead(out var normalEvent))
                    {
                        await handler(normalEvent);
                    }
                }

                cts.CancelAfter(TimeSpan.FromSeconds(5));
            }
        }
        catch (OperationCanceledException)
        {
            // Normal timeout, continue
        }
    }
}
