namespace AlpacaFleece.Core.Models;

/// <summary>
/// Alpaca order status enumeration with 11 states.
/// Maps Alpaca statuses: pending_new, accepted, pending_cancel, canceled, expired,
/// filled, partially_filled, pending_replace, replaced, rejected, suspended.
/// </summary>
/// <example>
/// <code>
/// var status = OrderState.Filled;
/// if (status == OrderState.Filled)
/// {
///     Console.WriteLine("Order completed");
/// }
/// </code>
/// </example>
public enum OrderState
{
    /// <summary>Order has been submitted to the broker (Alpaca: pending_new).</summary>
    PendingNew = 0,

    /// <summary>Order has been accepted by the broker (Alpaca: accepted).</summary>
    Accepted = 1,

    /// <summary>Cancel request has been submitted (Alpaca: pending_cancel).</summary>
    PendingCancel = 2,

    /// <summary>Order has been cancelled (Alpaca: canceled) — terminal state.</summary>
    Canceled = 3,

    /// <summary>Order expired without fill (Alpaca: expired) — terminal state.</summary>
    Expired = 4,

    /// <summary>Order has been fully filled (Alpaca: filled) — terminal state.</summary>
    Filled = 5,

    /// <summary>Order has been partially filled (Alpaca: partially_filled) — non-terminal state.</summary>
    PartiallyFilled = 6,

    /// <summary>Replace request has been submitted (Alpaca: pending_replace).</summary>
    PendingReplace = 7,

    /// <summary>Order has been replaced (Alpaca: replaced) — terminal state.</summary>
    Replaced = 8,

    /// <summary>Order has been rejected by the broker (Alpaca: rejected) — terminal state.</summary>
    Rejected = 9,

    /// <summary>Order has been suspended (Alpaca: suspended) — non-terminal state.</summary>
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
