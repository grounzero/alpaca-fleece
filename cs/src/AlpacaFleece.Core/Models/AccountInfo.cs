namespace AlpacaFleece.Core.Models;

/// <summary>
/// Account information snapshot from Alpaca API.
/// </summary>
public sealed record AccountInfo(
    string AccountId,
    decimal CashAvailable,
    decimal CashReserved,
    decimal PortfolioValue,
    decimal DayTradeCount,
    bool IsTradable,
    bool IsAccountRestricted,
    DateTimeOffset FetchedAt);
