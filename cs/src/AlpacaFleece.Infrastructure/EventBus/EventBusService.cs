namespace AlpacaFleece.Infrastructure.EventBus;

/// <summary>
/// Event bus implementation with dual-channel architecture.
/// Normal events: bounded channel (capacity=10000); published via TryWrite and dropped when full.
/// FullMode.Wait is configured so TryWrite fails on full instead of silently dropping.
/// Exit signals and order updates: unbounded channels, written with WriteAsync and never dropped.
/// </summary>
public sealed class EventBusService : IEventBus
{
    private readonly Channel<IEvent> _normalChannel;
    private readonly Channel<ExitSignalEvent> _exitChannel;
    private readonly Channel<OrderUpdateEvent> _orderUpdateChannel;
    private long _droppedCount;

    /// <summary>
    /// Gets the count of events dropped due to channel capacity constraints.
    /// </summary>
    public long DroppedCount => Volatile.Read(ref _droppedCount);

    /// <summary>
    /// Initialises a new instance of the <see cref="EventBusService"/> class with the specified normal channel capacity.
    /// </summary>
    /// <param name="normalChannelCapacity">The capacity of the normal event channel (default 10000).</param>
    public EventBusService(int normalChannelCapacity = 10000)
    {
        var normalOptions = new BoundedChannelOptions(normalChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false
        };
        _normalChannel = Channel.CreateBounded<IEvent>(normalOptions);

        _exitChannel = Channel.CreateUnbounded<ExitSignalEvent>();
        _orderUpdateChannel = Channel.CreateUnbounded<OrderUpdateEvent>();
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
        if (@event is OrderUpdateEvent orderUpdate)
        {
            return PublishOrderUpdateAsync(orderUpdate, ct);
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

    private async ValueTask<bool> PublishOrderUpdateAsync(OrderUpdateEvent orderUpdate, CancellationToken ct)
    {
        await _orderUpdateChannel.Writer.WriteAsync(orderUpdate, ct);
        return true;
    }

    private async ValueTask<bool> PublishExitSignalAsync(ExitSignalEvent exitSignal, CancellationToken ct)
    {
        await _exitChannel.Writer.WriteAsync(exitSignal, ct);
        return true;
    }

    /// <summary>
    /// Dispatches all events from both channels to handler.
    /// Priority: drain exit signals first, then order updates, then normal events.
    /// </summary>
    public async ValueTask DispatchAsync(Func<IEvent, ValueTask> handler, CancellationToken ct = default)
    {
        // Priority drain: exit signals first
        while (_exitChannel.Reader.TryRead(out var exitSignal))
        {
            await handler(exitSignal);
        }

        // Then order updates
        while (_orderUpdateChannel.Reader.TryRead(out var orderUpdate))
        {
            await handler(orderUpdate);
        }

        // Then normal events
        while (_normalChannel.Reader.TryRead(out var normalEvent))
        {
            await handler(normalEvent);
        }

        // Wait for new events
        while (!ct.IsCancellationRequested)
        {
            // Create a fresh timeout token for each iteration
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            
            try
            {
                // Check for exit signal
                var exitTask = _exitChannel.Reader.WaitToReadAsync(linkedCts.Token).AsTask();
                var orderUpdateTask = _orderUpdateChannel.Reader.WaitToReadAsync(linkedCts.Token).AsTask();
                var normalTask = _normalChannel.Reader.WaitToReadAsync(linkedCts.Token).AsTask();

                var completedTask = await Task.WhenAny(exitTask, orderUpdateTask, normalTask).ConfigureAwait(false);

                // Cancel the linked CTS so the non-selected wait is cancelled
                await linkedCts.CancelAsync();
                if (completedTask == exitTask && await exitTask)
                {
                    while (_exitChannel.Reader.TryRead(out var exitSignal))
                    {
                        await handler(exitSignal);
                    }
                }
                else if (completedTask == orderUpdateTask && await orderUpdateTask)
                {
                    while (_orderUpdateChannel.Reader.TryRead(out var orderUpdate))
                    {
                        await handler(orderUpdate);
                    }
                }
                else if (completedTask == normalTask && await normalTask)
                {
                    while (_normalChannel.Reader.TryRead(out var normalEvent))
                    {
                        await handler(normalEvent);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Only rethrow if the main CT was cancelled, not the timeout
                if (ct.IsCancellationRequested)
                {
                    throw;
                }
                // Otherwise, it's just a timeout - continue the loop
            }
        }
    }
}
