namespace AlpacaFleece.Core.Events;

/// <summary>
/// Emitted when a bar (OHLCV) arrives from market data stream.
/// </summary>
public sealed record BarEvent(
    string Symbol,
    string Timeframe,
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume) : IEvent;
