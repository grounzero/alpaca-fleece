namespace AlpacaFleece.Core.Models;

/// <summary>
/// Order information snapshot from Alpaca API.
/// </summary>
public sealed record OrderInfo(
    string AlpacaOrderId,
    string ClientOrderId,
    string Symbol,
    string Side, // "buy" or "sell"
    decimal Quantity,
    decimal FilledQuantity,
    decimal AverageFilledPrice,
    OrderState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
