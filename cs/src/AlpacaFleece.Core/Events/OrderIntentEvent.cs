namespace AlpacaFleece.Core.Events;

/// <summary>
/// Emitted when OrderManager decides to place an order (before submission to broker).
/// </summary>
public sealed record OrderIntentEvent(
    string Symbol,
    string Side, // "BUY" or "SELL"
    int Quantity,
    string ClientOrderId,
    DateTimeOffset CreatedAt) : IEvent;
