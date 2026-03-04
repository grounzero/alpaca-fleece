namespace AlpacaFleece.Core.Models;

/// <summary>
/// Mutable position data (current live state).
/// </summary>
public sealed class PositionData
{
    public string Symbol { get; set; } = string.Empty;
    public decimal CurrentQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal AtrValue { get; set; }
    public decimal TrailingStopPrice { get; set; }
    public DateTimeOffset LastUpdateAt { get; set; }

    // R-1: PendingExit is read/written from multiple threads (ExitManager + EventDispatcherService).
    // C# auto-properties cannot be volatile; use an explicit volatile backing field.
    private volatile bool _pendingExit;
    public bool PendingExit
    {
        get => _pendingExit;
        set => _pendingExit = value;
    }

    public PositionData() { }

    public PositionData(
        string symbol,
        decimal currentQuantity,
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
        _pendingExit = false;
    }
}
