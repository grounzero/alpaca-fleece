namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Polls Alpaca bars API for market data updates.
/// Batches symbols (25 per request by default).
/// Respects market hours (9:30-16:00 ET for stocks, 24/7 for crypto).
/// Publishes BarEvent to EventBus for each bar received.
/// Implements exponential backoff (3 retries, 100ms base, jitter).
/// </summary>
public sealed class StreamPollerService(
    IMarketDataClient marketDataClient,
    IBrokerService brokerService,
    IEventBus eventBus,
    IOptions<TradingOptions> tradingOptions,
    ILogger<StreamPollerService> logger) : BackgroundService
{
    private const int SymbolBatchSize = 25;
    private const int MaxRetries = 3;
    private const int BaseBackoffMs = 100;
    private int _backoffMs = BaseBackoffMs;
    private readonly Random _random = new();

    // Market hours config
    private static readonly TimeSpan MarketOpen = TimeSpan.Parse("09:30");
    private static readonly TimeSpan MarketClose = TimeSpan.Parse("16:00");
    private readonly TimeZoneInfo _eastTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("StreamPollerService starting");

        var symbols = tradingOptions.Value.Symbols.Symbols;
        var timeframe = "1m"; // Poll every 1 minute by default

        // Initialize data handler
        // var dataHandler = scope.ServiceProvider.GetRequiredService<IDataHandler>();
        // dataHandler.Initialize();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Check market hours
                if (!await IsMarketOpenAsync(stoppingToken))
                {
                    logger.LogDebug("Market is closed, skipping poll");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                    continue;
                }

                // Poll bars in batches
                await PollSymbolBatchesAsync(symbols, timeframe, stoppingToken);

                // Reset backoff on success
                _backoffMs = BaseBackoffMs;

                // Wait before next poll
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("StreamPollerService cancelled");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in streaming poll, retrying with backoff");

                // Exponential backoff with jitter
                var jitter = _random.Next(0, 50);
                var delay = Math.Min(_backoffMs + jitter, 5000); // Max 5s
                await Task.Delay(delay, stoppingToken);
                _backoffMs = Math.Min(_backoffMs * 2, 5000);
            }
        }

        logger.LogInformation("StreamPollerService stopped");
    }

    /// <summary>
    /// Checks if market is currently open based on broker clock.
    /// </summary>
    private async ValueTask<bool> IsMarketOpenAsync(CancellationToken ct)
    {
        try
        {
            var clock = await brokerService.GetClockAsync(ct);
            return clock.IsOpen;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check market clock");
            return false;
        }
    }

    /// <summary>
    /// Polls symbols in batches with retry logic.
    /// </summary>
    private async Task PollSymbolBatchesAsync(
        List<string> symbols,
        string timeframe,
        CancellationToken ct)
    {
        // Process in batches
        foreach (var batch in symbols.Batch(SymbolBatchSize))
        {
            var batchList = batch.ToList();
            var retryCount = 0;

            while (retryCount < MaxRetries)
            {
                try
                {
                    await PollBatchAsync(batchList, timeframe, ct);
                    break; // Success
                }
                catch (Exception ex) when (retryCount < MaxRetries - 1)
                {
                    retryCount++;
                    logger.LogWarning(
                        ex,
                        "Poll batch failed, retry {retry}/{max} for {count} symbols",
                        retryCount,
                        MaxRetries,
                        batchList.Count);

                    // Exponential backoff
                    var delay = (int)Math.Pow(2, retryCount) * 100 + _random.Next(0, 50);
                    await Task.Delay(delay, ct);
                }
            }
        }
    }

    /// <summary>
    /// Polls a single batch of symbols.
    /// </summary>
    private async Task PollBatchAsync(
        List<string> batch,
        string timeframe,
        CancellationToken ct)
    {
        logger.LogDebug("Polling batch of {count} symbols: {symbols}",
            batch.Count,
            string.Join(",", batch));

        foreach (var symbol in batch)
        {
            try
            {
                // Fetch latest bars (last 50 for now)
                var quotes = await marketDataClient.GetBarsAsync(symbol, timeframe, 50, ct);

                // Publish each as BarEvent (skip if backfilled)
                foreach (var quote in quotes)
                {
                    var barEvent = new BarEvent(
                        Symbol: quote.Symbol,
                        Timeframe: timeframe,
                        Timestamp: new DateTimeOffset(quote.Date, TimeSpan.Zero),
                        Open: quote.Open,
                        High: quote.High,
                        Low: quote.Low,
                        Close: quote.Close,
                        Volume: quote.Volume);

                    await eventBus.PublishAsync(barEvent, ct);
                }

                logger.LogDebug("Polled {symbol}: {count} bars", symbol, quotes.Count);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to poll {symbol}", symbol);
                throw;
            }
        }
    }
}

/// <summary>
/// Extension to batch enumerable.
/// </summary>
public static class EnumerableExtensions
{
    public static IEnumerable<IEnumerable<T>> Batch<T>(
        this IEnumerable<T> source,
        int batchSize)
    {
        var batch = new List<T>(batchSize);

        foreach (var item in source)
        {
            batch.Add(item);
            if (batch.Count == batchSize)
            {
                yield return batch;
                batch = new List<T>(batchSize);
            }
        }

        if (batch.Count > 0)
            yield return batch;
    }
}
