namespace AlpacaFleece.AdminUI.Models;

public sealed record AssetInfo(
    string Symbol,
    string Name,
    string Exchange,
    string AssetClass,
    bool Tradable);
