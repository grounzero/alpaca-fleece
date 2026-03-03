using System.Threading.Channels;

namespace AlpacaFleece.Infrastructure.EventBus;

/// <summary>
/// Event bus implementation with dual-channel architecture.
/// Normal events: bounded channel (capacity=10000); published via TryWrite and dropped when full.
/// FullMode.Wait is configured so TryWrite fails on full instead of silently dropping.
/// Exit signals: unbounded channel, written with WriteAsync and never dropped.
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
                var normalTask = _normalChannel.Reader.WaitToReadAsync(linkedCts.Token).AsTask();

                var completedTask = await Task.WhenAny(exitTask, normalTask).ConfigureAwait(false);

                // Cancel the linked CTS so the non-selected wait is cancelled
                await linkedCts.CancelAsync();
                if (completedTask == exitTask && await exitTask)
                {
                    while (_exitChannel.Reader.TryRead(out var exitSignal))
                    {
                        await handler(exitSignal);
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
