namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Order manager interface for deterministic order submission with idempotency.
/// Uses SHA-256 hash for client_order_id.
/// </summary>
public interface IOrderManager
{
    /// <summary>
    /// Submits an order intent (persists first, then submits to broker).
    /// Returns the generated client_order_id.
    /// </summary>
    ValueTask<string> SubmitSignalAsync(
        SignalEvent signal,
        int quantity,
        decimal limitPrice,
        CancellationToken ct = default);

    /// <summary>
    /// Submits an exit order for a symbol.
    /// </summary>
    ValueTask SubmitExitAsync(
        string symbol,
        string side,
        int quantity,
        decimal limitPrice,
        CancellationToken ct = default);

    /// <summary>
    /// Submits sell/buy orders for all open broker positions to flatten the portfolio.
    /// Per-symbol failures are logged and skipped rather than aborting the whole flatten.
    /// Returns the number of positions for which exit orders were submitted.
    /// </summary>
    ValueTask<int> FlattenPositionsAsync(CancellationToken ct = default);
}
