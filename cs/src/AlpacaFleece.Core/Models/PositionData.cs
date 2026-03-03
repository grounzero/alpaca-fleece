namespace AlpacaFleece.Core.Models;

/// <summary>
/// Mutable position data (current live state).
/// </summary>
public sealed class PositionData
{
    public string Symbol { get; set; } = string.Empty;
    public int CurrentQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal AtrValue { get; set; }
    public decimal TrailingStopPrice { get; set; }
    public DateTimeOffset LastUpdateAt { get; set; }
    public bool PendingExit { get; set; } = false;

    public PositionData() { }

    public PositionData(
        string symbol,
        int currentQuantity,
        decimal entryPrice,
        decimal atrValue,
        decimal trailingStopPrice)
    {
        Symbol = symbol;
        CurrentQuantity = currentQuantity;
        EntryPrice = entryPrice;
        AtrValue = atrValue;
        TrailingStopPrice = trailingStopPrice;
        LastUpdateAt = DateTimeOffset.UtcNow;
        PendingExit = false;
    }
}
