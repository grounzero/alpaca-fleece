namespace AlpacaFleece.Core.Events;

/// <summary>
/// Emitted when an order status changes (fill, cancellation, rejection).
/// </summary>
public sealed record OrderUpdateEvent(
    string AlpacaOrderId,
    string ClientOrderId,
    string Symbol,
    string Side,
    int FilledQuantity,
    int RemainingQuantity,
    decimal AverageFilledPrice,
    OrderState Status,
    DateTimeOffset UpdatedAt) : IEvent;
