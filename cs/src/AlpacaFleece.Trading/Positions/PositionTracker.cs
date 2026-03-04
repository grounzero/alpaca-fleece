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
    // Serialises concurrent open/close mutations so DB and in-memory state stay consistent
    // across background services (EventDispatcherService fills + RuntimeReconcilerService repairs).
    private readonly SemaphoreSlim _positionSemaphore = new(1, 1);

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
    /// Serialised by <see cref="_positionSemaphore"/> to prevent DB/memory inconsistency
    /// when EventDispatcherService and RuntimeReconcilerService both mutate the same symbol.
    /// </summary>
    public async ValueTask OpenPositionAsync(
        string symbol,
        decimal quantity,
        decimal entryPrice,
        decimal atrValue,
        CancellationToken ct = default)
    {
        await _positionSemaphore.WaitAsync(ct);
        try
        {
            var trailingStop = entryPrice - (atrValue * 1.5m);
            await _stateRepository.UpsertPositionTrackingAsync(symbol, quantity, entryPrice, atrValue, trailingStop, ct);
            OpenPositionInMemory(symbol, quantity, entryPrice, atrValue, trailingStop);
            logger.LogInformation("Position opened: {symbol} {qty} @ {price}", symbol, quantity, entryPrice);
        }
        finally
        {
            _positionSemaphore.Release();
        }
    }

    /// <summary>
    /// Closes a position: zeros DB row then removes from in-memory state.
    /// Serialised by <see cref="_positionSemaphore"/> to prevent DB/memory inconsistency.
    /// </summary>
    public async ValueTask ClosePositionAsync(string symbol, CancellationToken ct = default)
    {
        await _positionSemaphore.WaitAsync(ct);
        try
        {
            await _stateRepository.UpsertPositionTrackingAsync(symbol, 0m, 0m, 0m, 0m, ct);
            bool removed;
            lock (_lock)
                removed = _positions.Remove(symbol);
            if (removed)
                logger.LogInformation("Position closed: {symbol}", symbol);
        }
        finally
        {
            _positionSemaphore.Release();
        }
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
        // Hold the semaphore for the full rehydration so a concurrent OpenPositionAsync or
        // ClosePositionAsync (e.g. from RuntimeReconcilerService) cannot interleave with the
        // startup load and leave DB and in-memory state inconsistent.
        await _positionSemaphore.WaitAsync(ct);
        try
        {
            var rows = await _stateRepository.GetAllPositionTrackingAsync(ct);
            var loaded = 0;

            foreach (var (symbol, quantity, entryPrice, atrValue, trailingStop) in rows)
            {
                if (quantity > 0)
                {
                    // Use the persisted trailing stop so any tightening across restarts is preserved.
                    OpenPositionInMemory(symbol, quantity, entryPrice, atrValue, trailingStop);
                    loaded++;
                }
            }

            logger.LogInformation("PositionTracker rehydrated {count} position(s) from database", loaded);
        }
        finally
        {
            _positionSemaphore.Release();
        }
    }

    /// <summary>
    /// Sets the in-memory position without writing to the database.
    /// Used by <see cref="InitialiseFromDbAsync"/> (DB rows are already current on startup)
    /// and internally by <see cref="OpenPositionAsync"/> after persisting.
    /// </summary>
    private void OpenPositionInMemory(string symbol, decimal quantity, decimal entryPrice, decimal atrValue, decimal trailingStop)
    {
        var pos = new PositionData(symbol, quantity, entryPrice, atrValue, trailingStop);
        lock (_lock)
            _positions[symbol] = pos;
    }
}
