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
    private readonly object _lock = new();

    /// <summary>
    /// Returns a shallow snapshot of all current positions.
    /// The returned dictionary is a copy — adding/removing keys is safe concurrently.
    /// The <see cref="PositionData"/> values are the live shared objects; field reads and
    /// writes on them (e.g. <c>PendingExit</c>) are not protected by this lock and should
    /// only be performed by a single owner (ExitManager sets PendingExit after publish).
    /// </summary>
    public IReadOnlyDictionary<string, PositionData> GetAllPositions()
    {
        lock (_lock)
            return new Dictionary<string, PositionData>(_positions);
    }

    /// <summary>
    /// Gets position for symbol, or null if not open.
    /// </summary>
    public PositionData? GetPosition(string symbol)
    {
        lock (_lock)
        {
            _positions.TryGetValue(symbol, out var pos);
            return pos;
        }
    }

    /// <summary>
    /// Opens a position: persists to DB then updates in-memory state.
    /// </summary>
    public async ValueTask OpenPositionAsync(
        string symbol,
        decimal quantity,
        decimal entryPrice,
        decimal atrValue,
        CancellationToken ct = default)
    {
        var trailingStop = entryPrice - (atrValue * 1.5m);
        await _stateRepository.UpsertPositionTrackingAsync(symbol, quantity, entryPrice, atrValue, trailingStop, ct);
        OpenPositionInMemory(symbol, quantity, entryPrice, atrValue);
        logger.LogInformation("Position opened: {symbol} {qty} @ {price}", symbol, quantity, entryPrice);
    }

    /// <summary>
    /// Closes a position: zeros DB row then removes from in-memory state.
    /// </summary>
    public async ValueTask ClosePositionAsync(string symbol, CancellationToken ct = default)
    {
        await _stateRepository.UpsertPositionTrackingAsync(symbol, 0m, 0m, 0m, 0m, ct);
        bool removed;
        lock (_lock)
            removed = _positions.Remove(symbol);
        if (removed)
            logger.LogInformation("Position closed: {symbol}", symbol);
    }

    /// <summary>
    /// Updates trailing stop for a position.
    /// </summary>
    public void UpdateTrailingStop(string symbol, decimal newTrailingStop)
    {
        lock (_lock)
        {
            if (_positions.TryGetValue(symbol, out var pos))
            {
                pos.TrailingStopPrice = newTrailingStop;
                pos.LastUpdateAt = DateTimeOffset.UtcNow;
            }
        }
    }

    /// <summary>
    /// Rehydrates in-memory positions from the database.
    /// Mirrors Python's PositionTracker._load_from_db(): reads position_tracking rows
    /// and opens each live row (qty > 0) into in-memory state only (no DB write).
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
                OpenPositionInMemory(symbol, quantity, entryPrice, atrValue);
                loaded++;
            }
        }

        logger.LogInformation("PositionTracker rehydrated {count} position(s) from database", loaded);
    }

    /// <summary>
    /// Sets the in-memory position without writing to the database.
    /// Used by <see cref="InitialiseFromDbAsync"/> (DB rows are already current on startup)
    /// and internally by <see cref="OpenPositionAsync"/> after persisting.
    /// </summary>
    private void OpenPositionInMemory(string symbol, decimal quantity, decimal entryPrice, decimal atrValue)
    {
        var pos = new PositionData(symbol, quantity, entryPrice, atrValue, entryPrice - (atrValue * 1.5m));
        lock (_lock)
            _positions[symbol] = pos;
    }
}
