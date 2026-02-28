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
    /// Detects if symbol is equity (not crypto).
    /// </summary>
    bool IsEquity(string symbol);

    /// <summary>
    /// Detects if symbol is crypto.
    /// </summary>
    bool IsCrypto(string symbol);

    /// <summary>
    /// Normalises quote to standard format.
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
    decimal BidPrice,
    decimal AskPrice,
    decimal BidSize,
    decimal AskSize,
    DateTimeOffset Timestamp)
{
    /// <summary>
    /// Calculates spread percentage.
    /// </summary>
    public decimal SpreadPercent =>
        AskPrice > 0 && BidPrice > 0 ? ((AskPrice - BidPrice) / BidPrice) * 100m : 0m;

    /// <summary>
    /// Calculates mid price.
    /// </summary>
    public decimal MidPrice =>
        BidPrice > 0 && AskPrice > 0 ? (BidPrice + AskPrice) / 2m : 0m;
}

/// <summary>
/// Quote normalised to Skender format.
/// </summary>
public sealed record Quote(
    string Symbol,
    DateTimeOffset Timestamp,
    decimal Open,
    decimal High,
    decimal Low,
    decimal Close,
    long Volume);
