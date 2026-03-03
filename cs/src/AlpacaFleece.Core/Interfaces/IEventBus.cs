namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Thread-safe event bus with dual-channel architecture.
/// Normal events use a bounded channel (FullMode.DropWrite).
/// Exit signals use an unbounded channel (never dropped).
/// </summary>
public interface IEventBus
{
    /// <summary>
    /// Publishes an event to the appropriate channel.
    /// For ExitSignalEvent: unbounded, never drops.
    /// For other events: bounded, drops when full.
    /// Returns true if enqueued, false if dropped.
    /// </summary>
    ValueTask<bool> PublishAsync(IEvent @event, CancellationToken ct = default);

    /// <summary>
    /// Dispatches all events from the bus to handler.
    /// Called by EventDispatcher service.
    /// </summary>
    ValueTask DispatchAsync(Func<IEvent, ValueTask> handler, CancellationToken ct = default);

    /// <summary>
    /// Total count of events dropped due to bounded channel overflow.
    /// </summary>
    long DroppedCount { get; }
}
