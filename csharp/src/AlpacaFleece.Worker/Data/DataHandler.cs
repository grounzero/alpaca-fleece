namespace AlpacaFleece.Worker.Data;

/// <summary>
/// Routes bar events to per-symbol handlers.
/// Maintains in-memory bar history for each symbol (FIFO deque, 500 max).
/// Persists bars to SQLite asynchronously.
/// </summary>
public sealed class DataHandler(
    IEventBus eventBus,
    IDbContextFactory<TradingDbContext> dbContextFactory,
    ILogger<DataHandler> logger) : IDataHandler
{
    private readonly Dictionary<string, BarHistory> _barHistories = new();
    private readonly object _syncLock = new();

    /// <summary>
    /// Initializes handler (no-op, EventDispatcher handles event routing).
    /// </summary>
    public void Initialize()
    {
        logger.LogInformation("DataHandler initialized");
    }

    /// <summary>
    /// Handles a bar event (called by EventDispatcher via DispatchAsync).
    /// </summary>
    public async ValueTask OnBarAsync(BarEvent bar, CancellationToken ct)
    {
        await OnBarEventAsync(bar, ct);
    }

    /// <summary>
    /// Receives BarEvent, persists to DB (async), maintains in-memory deque.
    /// </summary>
    private async ValueTask OnBarEventAsync(BarEvent bar, CancellationToken ct)
    {
        try
        {
            lock (_syncLock)
            {
                // Ensure per-symbol history exists
                if (!_barHistories.TryGetValue(bar.Symbol, out var history))
                {
                    history = new BarHistory(500); // Max 500 bars per symbol
                    _barHistories[bar.Symbol] = history;
                }

                // Add to in-memory deque
                history.AddBar(bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
            }

            // Persist to SQLite asynchronously
            await PersistBarAsync(bar, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle bar for {symbol}", bar.Symbol);
            throw new DataHandlerException($"Failed to handle bar for {bar.Symbol}", ex);
        }
    }

    /// <summary>
    /// Persists bar to SQLite.
    /// </summary>
    private async ValueTask PersistBarAsync(BarEvent bar, CancellationToken ct)
    {
        try
        {
            var context = await dbContextFactory.CreateDbContextAsync(ct);
            if (context == null)
            {
                logger.LogWarning("DbContext factory returned null for {symbol}, skipping persistence", bar.Symbol);
                return;
            }

            using (context)
            {
                var barEntity = new BarEntity
                {
                    Symbol = bar.Symbol,
                    Timeframe = bar.Timeframe,
                    Timestamp = bar.Timestamp.UtcDateTime,
                    Open = bar.Open,
                    High = bar.High,
                    Low = bar.Low,
                    Close = bar.Close,
                    Volume = bar.Volume,
                    CreatedAt = DateTime.UtcNow
                };

                context.Bars.Add(barEntity);
                await context.SaveChangesAsync(ct);

                logger.LogDebug("Persisted bar for {symbol} at {time}", bar.Symbol, bar.Timestamp);
            }
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Database error persisting bar for {symbol}", bar.Symbol);
            throw;
        }
    }

    /// <summary>
    /// Gets in-memory bar history for a symbol.
    /// Returns snapshot of Quote list (conversion from internal format).
    /// </summary>
    public IReadOnlyList<Quote> GetDataFrame(string symbol)
    {
        lock (_syncLock)
        {
            if (!_barHistories.TryGetValue(symbol, out var history))
                return new List<Quote>();

            // Convert internal bar format to Quote
            var bars = history.GetBars();
            var quotes = new List<Quote>(bars.Count);

            var baseDate = DateTime.UtcNow.AddMinutes(-bars.Count);
            for (var i = 0; i < bars.Count; i++)
            {
                var bar = bars[i];
                quotes.Add(new Quote(
                    Symbol: symbol,
                    Date: baseDate.AddMinutes(i),
                    Open: bar.Open,
                    High: bar.High,
                    Low: bar.Low,
                    Close: bar.Close,
                    Volume: bar.Volume));
            }

            return quotes.AsReadOnly();
        }
    }

    /// <summary>
    /// Checks if sufficient history available for a symbol.
    /// </summary>
    public bool HasSufficientHistory(string symbol, int minBars)
    {
        lock (_syncLock)
        {
            return _barHistories.TryGetValue(symbol, out var history) && history.Count >= minBars;
        }
    }

    /// <summary>
    /// Gets bar count for a symbol.
    /// </summary>
    public int GetBarCount(string symbol)
    {
        lock (_syncLock)
        {
            return _barHistories.TryGetValue(symbol, out var history) ? history.Count : 0;
        }
    }

    /// <summary>
    /// Clears all in-memory histories (useful for testing).
    /// </summary>
    public void Clear()
    {
        lock (_syncLock)
        {
            _barHistories.Clear();
        }
    }
}

/// <summary>
/// DataHandler specific exceptions.
/// </summary>
public sealed class DataHandlerException(string message, Exception? innerException = null)
    : Exception(message, innerException);
