namespace AlpacaFleece.Worker.Metrics;

/// <summary>
/// Bot metrics: in-memory counters (thread-safe), persisted to data/metrics.json.
/// Tracks signals, orders, exits, dropped events, positions, P&L.
/// </summary>
public sealed class BotMetrics(ILogger<BotMetrics> logger)
{
    private long _signalsGenerated;
    private long _signalsFiltered;
    private long _ordersSubmitted;
    private long _ordersFilled;
    private long _exitsTriggered;
    private long _eventsDropped;
    private long _openPositions;
    private decimal _dailyPnl;
    private int _dailyTradeCount;
    private decimal _equityValue;
    private readonly object _decimalLock = new object();
    private readonly DateTimeOffset _sessionStartTime = DateTimeOffset.UtcNow;

    #region Counter Properties (Thread-Safe)

    public long SignalsGenerated => Interlocked.Read(ref _signalsGenerated);
    public long SignalsFiltered => Interlocked.Read(ref _signalsFiltered);
    public long OrdersSubmitted => Interlocked.Read(ref _ordersSubmitted);
    public long OrdersFilled => Interlocked.Read(ref _ordersFilled);
    public long ExitsTriggered => Interlocked.Read(ref _exitsTriggered);
    public long EventsDropped => Interlocked.Read(ref _eventsDropped);

    #endregion

    #region Gauge Properties

    public long OpenPositions => Interlocked.Read(ref _openPositions);

    public decimal DailyPnl
    {
        get
        {
            lock (_decimalLock)
                return _dailyPnl;
        }
        set
        {
            lock (_decimalLock)
                _dailyPnl = value;
        }
    }

    public int DailyTradeCount
    {
        get
        {
            lock (_decimalLock)
                return _dailyTradeCount;
        }
    }

    public decimal EquityValue
    {
        get
        {
            lock (_decimalLock)
                return _equityValue;
        }
        set
        {
            lock (_decimalLock)
                _equityValue = value;
        }
    }

    #endregion

    #region Increment Methods

    public void IncrementSignalsGenerated() => Interlocked.Increment(ref _signalsGenerated);
    public void IncrementSignalsFiltered() => Interlocked.Increment(ref _signalsFiltered);
    public void IncrementOrdersSubmitted() => Interlocked.Increment(ref _ordersSubmitted);
    public void IncrementOrdersFilled() => Interlocked.Increment(ref _ordersFilled);
    public void IncrementExitsTriggered() => Interlocked.Increment(ref _exitsTriggered);
    public void IncrementEventsDropped() => Interlocked.Increment(ref _eventsDropped);
    public void IncrementOpenPositions() => Interlocked.Increment(ref _openPositions);
    public void DecrementOpenPositions() => Interlocked.Decrement(ref _openPositions);

    public void SetDailyTradeCount(int count)
    {
        Interlocked.Exchange(ref _dailyTradeCount, count);
    }

    #endregion

    /// <summary>
    /// Writes metrics to data/metrics.json with session duration.
    /// </summary>
    public async ValueTask WriteToFileAsync(
        string? filePath = null,
        CancellationToken ct = default)
    {
        try
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);

            var outputPath = filePath ?? Path.Combine(dataDir, "metrics.json");
            var duration = DateTimeOffset.UtcNow - _sessionStartTime;

            var metrics = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                SessionDurationSeconds = duration.TotalSeconds,
                Counters = new
                {
                    SignalsGenerated,
                    SignalsFiltered,
                    OrdersSubmitted,
                    OrdersFilled,
                    ExitsTriggered,
                    EventsDropped
                },
                Gauges = new
                {
                    OpenPositions,
                    DailyPnl,
                    DailyTradeCount,
                    EquityValue
                },
                SessionStart = _sessionStartTime,
                SignalSuccessRate = SignalsGenerated > 0
                    ? (double)(SignalsGenerated - SignalsFiltered) / SignalsGenerated
                    : 0d
            };

            var json = System.Text.Json.JsonSerializer.Serialize(
                metrics,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(outputPath, json, ct);
            logger.LogDebug("Metrics written to {path}", outputPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write metrics");
        }
    }

    /// <summary>
    /// Returns a summary of current metrics.
    /// </summary>
    public string GetSummary()
    {
        var duration = DateTimeOffset.UtcNow - _sessionStartTime;
        var successRate = SignalsGenerated > 0
            ? (double)(SignalsGenerated - SignalsFiltered) / SignalsGenerated * 100
            : 0d;

        return $@"
=== AlpacaFleece Metrics Summary ===
Session Duration: {duration:hh\:mm\:ss}
Signals Generated: {SignalsGenerated}
Signals Filtered: {SignalsFiltered} ({successRate:F1}% pass rate)
Orders Submitted: {OrdersSubmitted}
Orders Filled: {OrdersFilled}
Exits Triggered: {ExitsTriggered}
Events Dropped: {EventsDropped}
Open Positions: {OpenPositions}
Daily P&L: {DailyPnl:C}
Daily Trades: {DailyTradeCount}
Equity: {EquityValue:C}
====================================";
    }
}
