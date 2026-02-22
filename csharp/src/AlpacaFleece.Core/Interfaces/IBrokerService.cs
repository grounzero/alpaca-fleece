namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Broker service for order execution and account queries.
/// Clock is never cached; always fresh.
/// Account and positions use 1s TTL cache.
/// </summary>
public interface IBrokerService
{
    /// <summary>
    /// Returns market clock status (always fresh, no cache).
    /// </summary>
    ValueTask<ClockInfo> GetClockAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns account info with 1s TTL cache.
    /// </summary>
    ValueTask<AccountInfo> GetAccountAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns all open positions with 1s TTL cache.
    /// </summary>
    ValueTask<IReadOnlyList<PositionInfo>> GetPositionsAsync(CancellationToken ct = default);

    /// <summary>
    /// Submits a limit order (no retry).
    /// </summary>
    ValueTask<OrderInfo> SubmitOrderAsync(
        string symbol,
        string side,
        int quantity,
        decimal limitPrice,
        string clientOrderId,
        CancellationToken ct = default);

    /// <summary>
    /// Cancels an order by Alpaca order ID (no retry).
    /// </summary>
    ValueTask CancelOrderAsync(string alpacaOrderId, CancellationToken ct = default);

    /// <summary>
    /// Fetches all open orders (with Polly retry).
    /// </summary>
    ValueTask<IReadOnlyList<OrderInfo>> GetOpenOrdersAsync(CancellationToken ct = default);
}
