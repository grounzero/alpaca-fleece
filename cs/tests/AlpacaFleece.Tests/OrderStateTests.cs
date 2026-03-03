namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for OrderState enumeration and extensions.
/// </summary>
public sealed class OrderStateTests
{
    // [Fact]
    // FromAlpaca method does not exist in OrderState
    // public void FromAlpaca_MapsAllStates()
    // {
    //     var mappings = new Dictionary<string, OrderState>
    //     {
    //         ["pending_new"] = OrderState.PendingNew,
    //         ["accepted"] = OrderState.Accepted,
    //         ["pending_cancel"] = OrderState.PendingCancel,
    //         ["canceled"] = OrderState.Canceled,
    //         ["expired"] = OrderState.Expired,
    //         ["filled"] = OrderState.Filled,
    //         ["partially_filled"] = OrderState.PartiallyFilled,
    //         ["pending_replace"] = OrderState.PendingReplace,
    //         ["replaced"] = OrderState.Replaced,
    //         ["rejected"] = OrderState.Rejected,
    //         ["suspended"] = OrderState.Suspended,
    //     };
    //
    //     foreach (var kvp in mappings)
    //     {
    //         var result = OrderState.FromAlpaca(kvp.Key);
    //         Assert.Equal(kvp.Value, result);
    //     }
    // }

    [Fact]
    public void IsTerminal_IdentifiesTerminalStates()
    {
        var terminalStates = new[]
        {
            OrderState.Canceled,
            OrderState.Expired,
            OrderState.Filled,
            OrderState.PartiallyFilled,
            OrderState.Rejected,
            OrderState.Suspended,
            OrderState.Replaced,
        };

        foreach (var state in terminalStates)
        {
            Assert.True(state.IsTerminal(), $"{state} should be terminal");
        }

        Assert.False(OrderState.PendingNew.IsTerminal());
        Assert.False(OrderState.Accepted.IsTerminal());
        Assert.False(OrderState.PendingCancel.IsTerminal());
        Assert.False(OrderState.PendingReplace.IsTerminal());
    }

    [Fact]
    public void HasFillPotential_IdentifiesOpenStates()
    {
        var openStates = new[]
        {
            OrderState.PendingNew,
            OrderState.Accepted,
            OrderState.PendingCancel,
            OrderState.PendingReplace,
            OrderState.PartiallyFilled,
        };

        foreach (var state in openStates)
        {
            Assert.True(state.HasFillPotential(), $"{state} should have fill potential");
        }

        Assert.False(OrderState.Canceled.HasFillPotential());
        Assert.False(OrderState.Filled.HasFillPotential());
        Assert.False(OrderState.Rejected.HasFillPotential());
    }

    [Fact]
    public void IsPartialTerminal_IdentifiesPartialFills()
    {
        // Partial fill: 0 < filledQty < orderQty
        Assert.True(OrderState.PartiallyFilled.IsPartialTerminal(50, 100));

        // Full fill
        Assert.False(OrderState.Filled.IsPartialTerminal(100, 100));

        // No fill
        Assert.False(OrderState.Filled.IsPartialTerminal(0, 100));

        // Non-terminal
        Assert.False(OrderState.Accepted.IsPartialTerminal(50, 100));
    }
}
