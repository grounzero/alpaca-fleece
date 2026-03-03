namespace AlpacaFleece.Trading.Positions;

/// <summary>
/// Position tracker: in-memory + SQLite persistence.
/// Tracks qty, entry price, ATR, trailing stop per symbol.
/// </summary>
public class PositionTracker(IStateRepository stateRepository, ILogger<PositionTracker> logger) : IPositionTracker
{
    // Protected no-arg constructor for NSubstitute proxy creation
    protected PositionTracker() : this(null!, null!) { }

    private readonly Dictionary<string, PositionData> _positions = new();
    private readonly IStateRepository _stateRepository = stateRepository;

    /// <summary>
    /// Gets all current positions.
    /// </summary>
    public IReadOnlyDictionary<string, PositionData> GetAllPositions() => _positions;

    /// <summary>
    /// Gets position for symbol, or null if not open.
    /// </summary>
    public PositionData? GetPosition(string symbol)
    {
        _positions.TryGetValue(symbol, out var pos);
        return pos;
    }

    /// <summary>
    /// Opens a position.
    /// </summary>
    public void OpenPosition(string symbol, int quantity, decimal entryPrice, decimal atrValue)
    {
        var pos = new PositionData(symbol, quantity, entryPrice, atrValue, entryPrice - (atrValue * 1.5m));
        _positions[symbol] = pos;
        logger.LogInformation("Position opened: {symbol} {qty} @ {price}", symbol, quantity, entryPrice);
    }

    /// <summary>
    /// Closes a position.
    /// </summary>
    public void ClosePosition(string symbol)
    {
        if (_positions.Remove(symbol))
        {
            logger.LogInformation("Position closed: {symbol}", symbol);
        }
    }

    /// <summary>
    /// Updates trailing stop for a position.
    /// </summary>
    public void UpdateTrailingStop(string symbol, decimal newTrailingStop)
    {
        if (_positions.TryGetValue(symbol, out var pos))
        {
            pos.TrailingStopPrice = newTrailingStop;
            pos.LastUpdateAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Rehydrates in-memory positions from the database.
    /// Mirrors Python's PositionTracker._load_from_db(): reads position_tracking rows
    /// and calls OpenPosition for each live row (qty > 0).
    /// Call this once at startup before the main trading loop begins.
    /// </summary>
    public async ValueTask InitialiseFromDbAsync(CancellationToken ct = default)
    {
        var rows = await _stateRepository.GetAllPositionTrackingAsync(ct);
        var loaded = 0;

        foreach (var (symbol, quantity, entryPrice, atrValue) in rows)
        {
            if (quantity > 0)
            {
                OpenPosition(symbol, quantity, entryPrice, atrValue);
                loaded++;
            }
        }

        logger.LogInformation("PositionTracker rehydrated {count} position(s) from database", loaded);
    }
}
