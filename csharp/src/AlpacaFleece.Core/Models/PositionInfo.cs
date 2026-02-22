namespace AlpacaFleece.Core.Models;

/// <summary>
/// Position snapshot from Alpaca API.
/// </summary>
public sealed record PositionInfo(
    string Symbol,
    int Quantity,
    decimal AverageEntryPrice,
    decimal CurrentPrice,
    decimal UnrealizedPnl,
    decimal UnrealizedPnlPercent,
    DateTimeOffset FetchedAt);
