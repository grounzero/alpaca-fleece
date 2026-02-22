namespace AlpacaFleece.Core.Models;

/// <summary>
/// Order information snapshot from Alpaca API.
/// </summary>
public sealed record OrderInfo(
    string AlpacaOrderId,
    string ClientOrderId,
    string Symbol,
    string Side, // "buy" or "sell"
    int Quantity,
    int FilledQuantity,
    decimal AverageFilledPrice,
    OrderState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
