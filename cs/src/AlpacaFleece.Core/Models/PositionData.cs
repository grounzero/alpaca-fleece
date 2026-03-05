namespace AlpacaFleece.Core.Models;

/// <summary>
/// Mutable position data (current live state).
/// Tracks open position details including entry price, current quantity, and stop/profit levels.
/// </summary>
/// <example>
/// <code>
/// var position = new PositionData(
///     symbol: "AAPL",
///     currentQuantity: 100m,
///     entryPrice: 150.25m,
///     atrValue: 2.5m,
///     trailingStopPrice: 145.00m
/// );
/// Console.WriteLine($"Position: {position.Symbol} qty={position.CurrentQuantity}");
/// </code>
/// </example>
public sealed class PositionData
{
    /// <summary>
    /// Gets or sets the trading symbol (e.g., AAPL, BTC/USD).
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current quantity held.
    /// </summary>
    public decimal CurrentQuantity { get; set; }

    /// <summary>
    /// Gets or sets the entry price at which the position was opened.
    /// </summary>
    public decimal EntryPrice { get; set; }

    /// <summary>
    /// Gets or sets the Average True Range (ATR) value used for stop/target calculations.
    /// </summary>
    public decimal AtrValue { get; set; }

    /// <summary>
    /// Gets or sets the trailing stop price level.
    /// </summary>
    public decimal TrailingStopPrice { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last update.
    /// </summary>
    public DateTimeOffset LastUpdateAt { get; set; }

    // R-1: PendingExit is read/written from multiple threads (ExitManager + EventDispatcherService).
    // C# auto-properties cannot be volatile; use an explicit volatile backing field.
    private volatile bool _pendingExit;

    /// <summary>
    /// Gets or sets a value indicating whether an exit signal has been sent but not yet executed.
    /// Thread-safe for concurrent reads/writes across ExitManager and EventDispatcherService.
    /// </summary>
    public bool PendingExit
    {
        get => _pendingExit;
        set => _pendingExit = value;
    }

    /// <summary>
    /// Initialises a new instance of the <see cref="PositionData"/> class with default values.
    /// </summary>
    public PositionData() { }

    /// <summary>
    /// Initialises a new instance of the <see cref="PositionData"/> class with specified position details.
    /// </summary>
    /// <param name="symbol">The trading symbol.</param>
    /// <param name="currentQuantity">The current quantity held.</param>
    /// <param name="entryPrice">The entry price at which the position was opened.</param>
    /// <param name="atrValue">The Average True Range value for stop/target calculations.</param>
    /// <param name="trailingStopPrice">The trailing stop price level.</param>
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
