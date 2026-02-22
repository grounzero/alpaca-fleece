namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Market data client interface for bar history and snapshots.
/// </summary>
public interface IMarketDataClient
{
    /// <summary>
    /// Gets bar history for a symbol.
    /// </summary>
    ValueTask<IReadOnlyList<Quote>> GetBarsAsync(
        string symbol,
        string timeframe,
        int limit,
        CancellationToken ct = default);

    /// <summary>
    /// Gets bid/ask snapshot for a symbol.
    /// </summary>
    ValueTask<BidAskSpread> GetSnapshotAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Detects if symbol is equity.
    /// </summary>
    bool IsEquity(string symbol);

    /// <summary>
    /// Detects if symbol is crypto.
    /// </summary>
    bool IsCrypto(string symbol);

    /// <summary>
    /// Normalizes quote to standard format.
    /// </summary>
    Quote NormalizeQuote(
        string symbol,
        decimal open,
        decimal high,
        decimal low,
        decimal close,
        long volume,
        DateTimeOffset timestamp);
}

/// <summary>
/// Bid/ask spread snapshot.
/// </summary>
public sealed record BidAskSpread(
    string Symbol,
    decimal Bid,
    decimal Ask,
    long BidSize,
    long AskSize,
    DateTimeOffset FetchedAt)
{
    /// <summary>
    /// Calculates spread percentage.
    /// </summary>
    public decimal SpreadPercent =>
        Ask > 0 && Bid > 0 ? ((Ask - Bid) / Bid) * 100m : 0m;

    /// <summary>
    /// Calculates mid price.
    /// </summary>
    public decimal MidPrice =>
        Bid > 0 && Ask > 0 ? (Bid + Ask) / 2m : 0m;
}

/// <summary>
/// Quote normalized to Skender format.
/// </summary>
public sealed record Quote(
    string Symbol,
    DateTime Date,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);
