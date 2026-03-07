namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Position tracker interface: in-memory + SQLite persistence.
/// Tracks qty, entry price, ATR, trailing stop per symbol.
/// </summary>
public interface IPositionTracker
{
    /// <summary>
    /// Gets all current positions.
    /// </summary>
    IReadOnlyDictionary<string, PositionData> GetAllPositions();

    /// <summary>
    /// Gets position for symbol, or null if not open.
    /// </summary>
    PositionData? GetPosition(string symbol);

    /// <summary>
    /// Opens a position (persists to DB then updates in-memory state).
    /// </summary>
    ValueTask OpenPositionAsync(string symbol, decimal quantity, decimal entryPrice, decimal atrValue, CancellationToken ct = default);

    /// <summary>
    /// Closes a position (zeros DB row then removes from in-memory state).
    /// </summary>
    ValueTask ClosePositionAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Updates trailing stop for a position (persists to DB).
    /// </summary>
    ValueTask UpdateTrailingStopAsync(string symbol, decimal newTrailingStop, CancellationToken ct = default);

    /// <summary>
    /// Updates the quantity and average entry price of an existing open position (partial fill).
    /// Does not change AtrValue or trailing stop.
    /// No-op if no open position exists for the symbol.
    /// </summary>
    ValueTask UpdateQuantityAsync(string symbol, decimal newQty, decimal avgPrice, CancellationToken ct = default);

    /// <summary>
    /// Rehydrates in-memory positions from the database.
    /// Must be called once at startup before the main trading loop begins.
    /// Mirrors Python's PositionTracker._load_from_db().
    /// </summary>
    ValueTask InitialiseFromDbAsync(CancellationToken ct = default);
}
