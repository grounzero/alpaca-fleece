namespace AlpacaFleece.Core.Models;

/// <summary>
/// Broad asset class categories for portfolio concentration limits.
/// </summary>
public enum AssetClass
{
    /// <summary>Exchange-listed equities and equity ETFs.</summary>
    Equity,

    /// <summary>Government and corporate bonds and bond ETFs.</summary>
    Bond,

    /// <summary>Cryptocurrency pairs traded on the Alpaca platform (e.g. BTC/USD).</summary>
    Crypto,

    /// <summary>Physical commodities and commodity ETFs (gold, oil, etc.).</summary>
    Commodity,

    /// <summary>Real estate investment trusts and property ETFs.</summary>
    RealEstate,
}
