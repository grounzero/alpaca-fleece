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
    /// Opens a position.
    /// </summary>
    void OpenPosition(string symbol, int quantity, decimal entryPrice, decimal atrValue);

    /// <summary>
    /// Closes a position.
    /// </summary>
    void ClosePosition(string symbol);

    /// <summary>
    /// Updates trailing stop for a position.
    /// </summary>
    void UpdateTrailingStop(string symbol, decimal newTrailingStop);

    /// <summary>
    /// Rehydrates in-memory positions from the database.
    /// Must be called once at startup before the main trading loop begins.
    /// Mirrors Python's PositionTracker._load_from_db().
    /// </summary>
    ValueTask InitialiseFromDbAsync(CancellationToken ct = default);
}
