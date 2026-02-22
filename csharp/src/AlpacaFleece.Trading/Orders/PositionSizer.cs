namespace AlpacaFleece.Trading.Orders;

/// <summary>
/// Position sizing calculator.
/// Determines order quantity based on account equity, risk limits, and current price.
/// </summary>
public sealed class PositionSizer
{
    /// <summary>
    /// Calculates the position size (quantity) for a signal.
    /// Formula: qty = (account_equity * max_position_pct) / current_price
    /// Enforces: qty >= 1 (minimum for Alpaca), qty <= max allowed
    /// </summary>
    /// <param name="signal">The signal event with current price</param>
    /// <param name="accountEquity">Total account equity</param>
    /// <param name="maxPositionPct">Max position as % of account (e.g., 0.05 = 5%)</param>
    /// <returns>Calculated quantity, at least 1 share</returns>
    public static decimal CalculateQuantity(
        SignalEvent signal,
        decimal accountEquity,
        decimal maxPositionPct = 0.05m)
    {
        if (signal == null)
            throw new ArgumentNullException(nameof(signal));

        if (accountEquity <= 0)
            throw new ArgumentException("Account equity must be positive", nameof(accountEquity));

        if (maxPositionPct <= 0 || maxPositionPct > 1)
            throw new ArgumentException("Max position percent must be between 0 and 1", nameof(maxPositionPct));

        if (signal.Metadata.CurrentPrice <= 0)
            throw new ArgumentException("Current price must be positive", nameof(signal));

        // Calculate max quantity based on risk limit
        var maxQty = (accountEquity * maxPositionPct) / signal.Metadata.CurrentPrice;

        // Ensure at least 1 share and enforce as integer
        var qty = Math.Max(1m, Math.Floor(maxQty));

        return qty;
    }

    /// <summary>
    /// Validates if a proposed quantity respects position limits.
    /// </summary>
    /// <param name="proposedQty">Proposed quantity to validate</param>
    /// <param name="maxQty">Maximum allowed quantity</param>
    /// <returns>True if valid, false otherwise</returns>
    public static bool IsValidQuantity(decimal proposedQty, decimal maxQty)
    {
        return proposedQty >= 1 && proposedQty <= maxQty;
    }
}
