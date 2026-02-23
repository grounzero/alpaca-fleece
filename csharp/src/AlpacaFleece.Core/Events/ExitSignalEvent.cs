namespace AlpacaFleece.Core.Events;

/// <summary>
/// Emitted by ExitManager when a position should be closed (stop loss, trailing stop, profit target).
/// Never dropped from event bus; uses unbounded channel.
/// </summary>
public sealed record ExitSignalEvent(
    string Symbol,
    string ExitReason, // "STOP_LOSS", "TRAILING_STOP", "PROFIT_TARGET"
    decimal ExitPrice,
    DateTimeOffset CreatedAt) : IEvent;
