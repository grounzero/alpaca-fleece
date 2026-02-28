namespace AlpacaFleece.Worker.Data;

/// <summary>
/// Receives BarEvent, persists to SQLite, maintains in-memory deque per symbol.
/// Acts as an intermediary between data polling and bar history management.
/// Publishes persisted bars for strategy consumption.
/// </summary>
public sealed class BarsHandler(
    IEventBus eventBus,
    IDbContextFactory<TradingDbContext> dbContextFactory,
    ILogger<BarsHandler> logger) : BackgroundService
{
    private readonly Dictionary<string, Queue<(decimal O, decimal H, decimal L, decimal C, long V)>> _symbolDeques = new();
    private const int MaxDequeSize = 500;

    /// <summary>
    /// Initializes handler: loads historical bars from SQLite, then waits.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await LoadHistoricalBarsAsync(stoppingToken);
        logger.LogInformation("BarsHandler initialized");
        await Task.Delay(Timeout.Infinite, stoppingToken);
    }

    /// <summary>
    /// Loads the last <see cref="MaxDequeSize"/> bars per symbol from SQLite into memory.
    /// Errors are logged as warnings and never crash startup.
    /// </summary>
    internal async ValueTask LoadHistoricalBarsAsync(CancellationToken ct)
    {
        try
        {
            using var context = await dbContextFactory.CreateDbContextAsync(ct);

            var symbols = await context.Bars
                .Select(b => b.Symbol)
                .Distinct()
                .ToListAsync(ct);

            foreach (var symbol in symbols)
            {
                var bars = await context.Bars
                    .Where(b => b.Symbol == symbol)
                    .OrderByDescending(b => b.Timestamp)
                    .Take(MaxDequeSize)
                    .OrderBy(b => b.Timestamp)
                    .ToListAsync(ct);

                lock (_symbolDeques)
                {
                    var deque = new Queue<(decimal, decimal, decimal, decimal, long)>(bars.Count);
                    foreach (var bar in bars)
                        deque.Enqueue((bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));
                    _symbolDeques[symbol] = deque;
                }

                logger.LogInformation("Loaded {Count} historical bars for {Symbol}", bars.Count, symbol);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to load historical bars; continuing with empty deques");
        }
    }

    /// <summary>
    /// Handles incoming BarEvent: persists to DB and maintains deque.
    /// </summary>
    private async ValueTask HandleBarEventAsync(BarEvent bar, CancellationToken ct)
    {
        try
        {
            // Persist to SQLite
            await PersistBarToDbAsync(bar, ct);

            // Maintain in-memory deque per symbol
            lock (_symbolDeques)
            {
                if (!_symbolDeques.TryGetValue(bar.Symbol, out var deque))
                {
                    deque = new Queue<(decimal, decimal, decimal, decimal, long)>(MaxDequeSize);
                    _symbolDeques[bar.Symbol] = deque;
                }

                // Add to deque
                deque.Enqueue((bar.Open, bar.High, bar.Low, bar.Close, bar.Volume));

                // Remove oldest if full
                if (deque.Count > MaxDequeSize)
                    deque.Dequeue();
            }

            logger.LogDebug("Handled bar for {symbol} at {time}: {open}/{high}/{low}/{close}",
                bar.Symbol, bar.Timestamp, bar.Open, bar.High, bar.Low, bar.Close);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle bar for {symbol}", bar.Symbol);
            throw new BarsHandlerException($"Failed to handle bar for {bar.Symbol}", ex);
        }
    }

    /// <summary>
    /// Persists bar to SQLite bars table.
    /// </summary>
    private async ValueTask PersistBarToDbAsync(BarEvent bar, CancellationToken ct)
    {
        try
        {
            using var context = await dbContextFactory.CreateDbContextAsync(ct);

            // Check if bar already exists (idempotency)
            var existing = await context.Bars
                .Where(b => b.Symbol == bar.Symbol &&
                           b.Timeframe == bar.Timeframe &&
                           b.Timestamp == bar.Timestamp.UtcDateTime)
                .FirstOrDefaultAsync(ct);

            if (existing != null)
            {
                logger.LogDebug("Bar already exists for {symbol} at {time}", bar.Symbol, bar.Timestamp);
                return;
            }

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
        catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("UNIQUE constraint failed") == true)
        {
            // Ignore duplicate key errors (idempotency)
            logger.LogDebug("Duplicate bar for {symbol}, ignoring", bar.Symbol);
        }
        catch (DbUpdateException ex)
        {
            logger.LogError(ex, "Database error persisting bar for {symbol}", bar.Symbol);
            throw;
        }
    }

    /// <summary>
    /// Gets bars for a symbol from in-memory deque.
    /// </summary>
    public IReadOnlyList<(decimal O, decimal H, decimal L, decimal C, long V)> GetBarsForSymbol(string symbol)
    {
        lock (_symbolDeques)
        {
            if (!_symbolDeques.TryGetValue(symbol, out var deque))
                return new List<(decimal, decimal, decimal, decimal, long)>();

            return deque.ToList().AsReadOnly();
        }
    }

    /// <summary>
    /// Gets bar count for a symbol.
    /// </summary>
    public int GetBarCount(string symbol)
    {
        lock (_symbolDeques)
        {
            return _symbolDeques.TryGetValue(symbol, out var deque) ? deque.Count : 0;
        }
    }

    /// <summary>
    /// Clears all deques (useful for testing).
    /// </summary>
    public void Clear()
    {
        lock (_symbolDeques)
        {
            _symbolDeques.Clear();
        }
    }
}

/// <summary>
/// BarsHandler specific exceptions.
/// </summary>
public sealed class BarsHandlerException(string message, Exception? innerException = null)
    : Exception(message, innerException);
