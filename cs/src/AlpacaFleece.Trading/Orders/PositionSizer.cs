namespace AlpacaFleece.Trading.Orders;

/// <summary>
/// Position sizing calculator.
/// Determines order quantity based on account equity, risk limits, and current price.
/// </summary>
public sealed class PositionSizer
{
    /// <summary>
    /// Calculates the position size (quantity) for a signal.
    /// Formula: qty = floor(account_equity x max_position_pct / current_price)
    /// Enforces: qty at least 1 (minimum for Alpaca)
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
    /// Calculates position size using a dual formula: the minimum of the equity-based cap and the
    /// risk-based cap. Mirrors Python's position_sizer logic.
    ///
    /// Equity formula: qty = floor(equity * maxPositionPct / price)
    /// Risk formula:   qty = floor(equity * maxRiskPerTradePct / (price * stopLossPct))
    /// Result:         min(equity_qty, risk_qty), at least 1
    ///
    /// The risk formula ensures the loss on a full stop-out never exceeds (equity * maxRiskPerTradePct).
    /// </summary>
    /// <param name="signal">Signal event with current price</param>
    /// <param name="accountEquity">Total account equity</param>
    /// <param name="maxPositionPct">Max position as fraction of equity (e.g. 0.05 = 5%)</param>
    /// <param name="maxRiskPerTradePct">Max risk per trade as fraction of equity (e.g. 0.01 = 1%)</param>
    /// <param name="stopLossPct">Expected stop-loss distance as fraction of price (e.g. 0.02 = 2%)</param>
    /// <returns>Calculated quantity, at least 1 share</returns>
    public static decimal CalculateQuantity(
        SignalEvent signal,
        decimal accountEquity,
        decimal maxPositionPct,
        decimal maxRiskPerTradePct,
        decimal stopLossPct)
    {
        if (signal == null)
            throw new ArgumentNullException(nameof(signal));

        if (accountEquity <= 0)
            throw new ArgumentException("Account equity must be positive", nameof(accountEquity));

        if (maxPositionPct <= 0 || maxPositionPct > 1)
            throw new ArgumentException("Max position percent must be between 0 and 1", nameof(maxPositionPct));

        if (maxRiskPerTradePct <= 0 || maxRiskPerTradePct > 1)
            throw new ArgumentException("Max risk per trade percent must be between 0 and 1", nameof(maxRiskPerTradePct));

        if (stopLossPct <= 0 || stopLossPct > 1)
            throw new ArgumentException("Stop loss percent must be between 0 and 1", nameof(stopLossPct));

        if (signal.Metadata.CurrentPrice <= 0)
            throw new ArgumentException("Current price must be positive", nameof(signal));

        var price = signal.Metadata.CurrentPrice;

        // Equity-based cap: don't allocate more than maxPositionPct of portfolio
        var equityQty = (accountEquity * maxPositionPct) / price;

        // Risk-based cap: cap loss on a full stop-out to maxRiskPerTradePct of equity
        var riskQty = (accountEquity * maxRiskPerTradePct) / (price * stopLossPct);

        var maxQty = Math.Min(equityQty, riskQty);
        return Math.Max(1m, Math.Floor(maxQty));
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
