namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Data handler for routing bar events and managing per-symbol history.
/// </summary>
public interface IDataHandler
{
    /// <summary>
    /// Initializes handler by subscribing to BarEvent.
    /// </summary>
    void Initialize();

    /// <summary>
    /// Gets in-memory bar history for a symbol as Quote list.
    /// </summary>
    IReadOnlyList<Quote> GetDataFrame(string symbol);

    /// <summary>
    /// Checks if sufficient history available for a symbol.
    /// </summary>
    bool HasSufficientHistory(string symbol, int minBars);

    /// <summary>
    /// Gets bar count for a symbol.
    /// </summary>
    int GetBarCount(string symbol);

    /// <summary>
    /// Clears all in-memory histories.
    /// </summary>
    void Clear();
}
