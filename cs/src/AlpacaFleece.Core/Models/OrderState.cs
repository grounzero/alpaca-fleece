namespace AlpacaFleece.Core.Models;

/// <summary>
/// Alpaca order status enumeration with 11 states.
/// Maps Alpaca statuses: pending_new, accepted, pending_cancel, canceled, expired,
/// filled, partially_filled, pending_replace, replaced, rejected, suspended.
/// </summary>
public enum OrderState
{
    PendingNew = 0,
    Accepted = 1,
    PendingCancel = 2,
    Canceled = 3,
    Expired = 4,
    Filled = 5,
    PartiallyFilled = 6,
    PendingReplace = 7,
    Replaced = 8,
    Rejected = 9,
    Suspended = 10,
}

/// <summary>
/// Extension methods for OrderState.
/// </summary>
public static class OrderStateExtensions
{
    /// <summary>
    /// Returns true if the order is in a terminal (final) state.
    /// Terminal states: Canceled, Expired, Filled, PartiallyFilled, Rejected, Suspended, Replaced.
    /// </summary>
    public static bool IsTerminal(this OrderState state) => state is
        OrderState.Canceled or
        OrderState.Expired or
        OrderState.Filled or
        OrderState.PartiallyFilled or
        OrderState.Rejected or
        OrderState.Suspended or
        OrderState.Replaced;

    /// <summary>
    /// Returns true if the order could potentially have future fills.
    /// </summary>
    public static bool HasFillPotential(this OrderState state) => state is
        OrderState.PendingNew or
        OrderState.Accepted or
        OrderState.PendingCancel or
        OrderState.PendingReplace or
        OrderState.PartiallyFilled;

    /// <summary>
    /// Maps Alpaca status string to OrderState.
    /// </summary>
    public static OrderState FromAlpaca(string alpacaStatus) => alpacaStatus.ToLowerInvariant() switch
    {
        "pending_new" => OrderState.PendingNew,
        "accepted" => OrderState.Accepted,
        "pending_cancel" => OrderState.PendingCancel,
        "canceled" => OrderState.Canceled,
        "expired" => OrderState.Expired,
        "filled" => OrderState.Filled,
        "partially_filled" => OrderState.PartiallyFilled,
        "pending_replace" => OrderState.PendingReplace,
        "replaced" => OrderState.Replaced,
        "rejected" => OrderState.Rejected,
        "suspended" => OrderState.Suspended,
        _ => throw new ArgumentException($"Unknown Alpaca order status: {alpacaStatus}", nameof(alpacaStatus)),
    };

    /// <summary>
    /// Detects if this is a partial-terminal state: terminal AND 0 &lt; filledQty &lt; orderQty.
    /// </summary>
    public static bool IsPartialTerminal(this OrderState state, int filledQuantity, int totalQuantity) =>
        state.IsTerminal() && filledQuantity > 0 && filledQuantity < totalQuantity;
}
