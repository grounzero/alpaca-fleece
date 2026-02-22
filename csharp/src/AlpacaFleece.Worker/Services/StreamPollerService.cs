namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Polls Alpaca for market data bar updates (every minute) and order status updates (every 2s).
///
/// Bar polling:
///   Batches symbols (25 per request by default).
///   Respects market hours (9:30-16:00 ET for stocks; order loop runs 24/7 for crypto fills).
///   Publishes BarEvent to EventBus for each new bar received.
///   Implements exponential backoff (3 retries, 100ms base, jitter).
///
/// Order polling:
///   Runs every 2 seconds regardless of market hours.
///   Fetches all non-terminal order intents from DB, queries Alpaca for current status.
///   Publishes OrderUpdateEvent when status changes (fill, cancel, rejection).
///   Inserts fill records via InsertFillIdempotentAsync for deduplication.
///   Maximum 10 concurrent Alpaca requests per cycle (matches Python order_polling_concurrency).
/// </summary>
public sealed class StreamPollerService(
    IMarketDataClient marketDataClient,
    IBrokerService brokerService,
    IStateRepository stateRepository,
    IEventBus eventBus,
    IOptions<TradingOptions> tradingOptions,
    ILogger<StreamPollerService> logger) : BackgroundService
{
    private const int SymbolBatchSize = 25;
    private const int MaxRetries = 3;
    private const int BaseBackoffMs = 100;
    private const int OrderPollConcurrency = 10;
    private int _backoffMs = BaseBackoffMs;
    private readonly Random _random = new();

    // Bar deduplication: tracks the timestamp of the last bar published per symbol.
    // Prevents re-publishing bars we've already emitted in a previous poll cycle.
    private readonly Dictionary<string, DateTimeOffset> _lastPublishedBarTs = new();

    private static readonly HashSet<OrderState> NonTerminalStates =
    [
        OrderState.PendingNew,
        OrderState.Accepted,
        OrderState.PartiallyFilled,
        OrderState.PendingCancel,
        OrderState.PendingReplace,
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("StreamPollerService starting");

        // Run bar polling and order polling concurrently; stop when either completes
        // (which only happens on cancellation or fatal error).
        await Task.WhenAll(
            RunBarPollLoopAsync(stoppingToken),
            RunOrderPollLoopAsync(stoppingToken));

        logger.LogInformation("StreamPollerService stopped");
    }

    // =========================================================================
    // Bar polling loop
    // =========================================================================

    private async Task RunBarPollLoopAsync(CancellationToken ct)
    {
        var allSymbols = tradingOptions.Value.Symbols.Symbols;
        var cryptoSymbols = new HashSet<string>(
            tradingOptions.Value.Symbols.CryptoSymbols,
            StringComparer.OrdinalIgnoreCase);
        const string timeframe = "1m";

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var marketOpen = await IsMarketOpenAsync(ct);

                // When market is closed: poll crypto only (24/7); when open: poll all symbols
                List<string> symbolsToPoll;
                if (!marketOpen)
                {
                    symbolsToPoll = allSymbols.Where(s => cryptoSymbols.Contains(s)).ToList();
                    if (symbolsToPoll.Count == 0)
                    {
                        logger.LogDebug("Market is closed and no crypto symbols configured, skipping bar poll");
                        await Task.Delay(TimeSpan.FromMinutes(1), ct);
                        continue;
                    }

                    logger.LogDebug("Market closed — polling {count} crypto symbol(s) only", symbolsToPoll.Count);
                }
                else
                {
                    symbolsToPoll = allSymbols;
                }

                await PollSymbolBatchesAsync(symbolsToPoll, timeframe, ct);

                // Reset backoff on success
                _backoffMs = BaseBackoffMs;

                await Task.Delay(TimeSpan.FromMinutes(1), ct);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Bar poll loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in bar poll loop, retrying with backoff");

                var jitter = _random.Next(0, 50);
                var delay = Math.Min(_backoffMs + jitter, 5000);
                await Task.Delay(delay, ct);
                _backoffMs = Math.Min(_backoffMs * 2, 5000);
            }
        }
    }

    /// <summary>
    /// Checks if the market is currently open via broker clock.
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
    /// Polls all configured symbols in batches with per-batch retry logic.
    /// </summary>
    private async Task PollSymbolBatchesAsync(
        List<string> symbols,
        string timeframe,
        CancellationToken ct)
    {
        foreach (var batch in symbols.Batch(SymbolBatchSize))
        {
            var batchList = batch.ToList();
            var retryCount = 0;

            while (retryCount < MaxRetries)
            {
                try
                {
                    await PollBatchAsync(batchList, timeframe, ct);
                    break;
                }
                catch (Exception ex) when (retryCount < MaxRetries - 1)
                {
                    retryCount++;
                    logger.LogWarning(
                        ex,
                        "Poll batch failed, retry {Retry}/{Max} for {Count} symbols",
                        retryCount,
                        MaxRetries,
                        batchList.Count);

                    var delay = (int)Math.Pow(2, retryCount) * 100 + _random.Next(0, 50);
                    await Task.Delay(delay, ct);
                }
            }
        }
    }

    /// <summary>
    /// Fetches the latest bars for a single batch and publishes a BarEvent per symbol.
    /// </summary>
    private async Task PollBatchAsync(
        List<string> batch,
        string timeframe,
        CancellationToken ct)
    {
        logger.LogDebug("Polling batch of {Count} symbols: {Symbols}",
            batch.Count,
            string.Join(",", batch));

        foreach (var symbol in batch)
        {
            try
            {
                var quotes = await marketDataClient.GetBarsAsync(symbol, timeframe, 50, ct);

                _lastPublishedBarTs.TryGetValue(symbol, out var lastTs);
                var newBars = 0;

                foreach (var quote in quotes)
                {
                    var barTs = new DateTimeOffset(quote.Date, TimeSpan.Zero);

                    // Skip bars we've already published (deduplication)
                    if (barTs <= lastTs)
                    {
                        continue;
                    }

                    var barEvent = new BarEvent(
                        Symbol: quote.Symbol,
                        Timeframe: timeframe,
                        Timestamp: barTs,
                        Open: quote.Open,
                        High: quote.High,
                        Low: quote.Low,
                        Close: quote.Close,
                        Volume: quote.Volume);

                    await eventBus.PublishAsync(barEvent, ct);
                    lastTs = barTs;
                    newBars++;
                }

                // Persist the latest timestamp for next cycle
                if (newBars > 0)
                {
                    _lastPublishedBarTs[symbol] = lastTs;
                }

                logger.LogDebug("Polled {Symbol}: {Total} bars, {New} new", symbol, quotes.Count, newBars);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to poll {Symbol}", symbol);
                throw;
            }
        }
    }

    // =========================================================================
    // Order update polling loop
    // =========================================================================

    /// <summary>
    /// Polls order status every 2 seconds, 24/7.
    /// Mirrors Python's _poll_order_updates: checks DB for non-terminal orders,
    /// queries Alpaca for current status, emits OrderUpdateEvent on any change.
    /// </summary>
    private async Task RunOrderPollLoopAsync(CancellationToken ct)
    {
        logger.LogInformation("Order poll loop starting");

        while (!ct.IsCancellationRequested)
        {
            var start = DateTimeOffset.UtcNow;

            try
            {
                await PollOrderUpdatesAsync(ct);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("Order poll loop cancelled");
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Order poll error");
            }

            // Maintain a roughly-fixed 2s cadence
            var elapsed = (DateTimeOffset.UtcNow - start).TotalSeconds;
            var toSleep = Math.Max(0.0, 2.0 - elapsed);
            await Task.Delay(TimeSpan.FromSeconds(toSleep), ct);
        }
    }

    /// <summary>
    /// One cycle of order update polling:
    /// 1. Loads all non-terminal order intents from DB.
    /// 2. Queries Alpaca for each order's current status (bounded concurrency).
    /// 3. On status change: persists to DB, records fill, publishes OrderUpdateEvent.
    /// Internal so tests can call it directly without waiting for the 2s loop cadence.
    /// </summary>
    internal async Task PollOrderUpdatesAsync(CancellationToken ct)
    {
        var allIntents = await stateRepository.GetAllOrderIntentsAsync(ct);

        var pending = allIntents
            .Where(o => NonTerminalStates.Contains(o.Status) &&
                        !string.IsNullOrEmpty(o.AlpacaOrderId))
            .ToList();

        if (pending.Count == 0)
            return;

        logger.LogDebug("Order poll: checking {Count} non-terminal orders", pending.Count);

        // Bound concurrency to 10 concurrent Alpaca calls (mirrors Python's order_polling_concurrency=10)
        var sem = new SemaphoreSlim(OrderPollConcurrency);
        var tasks = pending.Select(intent => ProcessOrderUpdateAsync(intent, sem, ct));
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// Processes one order: queries Alpaca, detects status change, persists, publishes.
    /// </summary>
    private async Task ProcessOrderUpdateAsync(
        OrderIntentDto intent,
        SemaphoreSlim sem,
        CancellationToken ct)
    {
        await sem.WaitAsync(ct);
        try
        {
            var order = await brokerService.GetOrderByIdAsync(intent.AlpacaOrderId!, ct);

            if (order == null)
            {
                logger.LogWarning(
                    "Order {ClientOrderId} (Alpaca: {AlpacaId}) not found in Alpaca",
                    intent.ClientOrderId,
                    intent.AlpacaOrderId);
                return;
            }

            // Only emit when status changes
            if (order.Status == intent.Status)
                return;

            logger.LogInformation(
                "Order {ClientOrderId} status: {Old} → {New} (filled: {Filled}/{Total})",
                intent.ClientOrderId,
                intent.Status,
                order.Status,
                order.FilledQuantity,
                intent.Quantity);

            // Persist updated status to DB
            await stateRepository.UpdateOrderIntentAsync(
                intent.ClientOrderId,
                order.AlpacaOrderId,
                order.Status,
                DateTimeOffset.UtcNow,
                ct);

            // Record fill for filled/partial-fill transitions (idempotent by dedupe key)
            if (order.FilledQuantity > 0 &&
                order.Status is OrderState.Filled or OrderState.PartiallyFilled)
            {
                var dedupeKey = $"{order.AlpacaOrderId}:{order.FilledQuantity}:{order.AverageFilledPrice}";
                await stateRepository.InsertFillIdempotentAsync(
                    order.AlpacaOrderId,
                    order.ClientOrderId,
                    order.FilledQuantity,
                    order.AverageFilledPrice,
                    dedupeKey,
                    DateTimeOffset.UtcNow,
                    ct);
            }

            // Publish event so OrderManager / PositionTracker can react
            var remaining = Math.Max(0, intent.Quantity - order.FilledQuantity);
            var ev = new OrderUpdateEvent(
                AlpacaOrderId: order.AlpacaOrderId,
                ClientOrderId: order.ClientOrderId,
                Symbol: order.Symbol,
                Side: order.Side,
                FilledQuantity: order.FilledQuantity,
                RemainingQuantity: remaining,
                AverageFilledPrice: order.AverageFilledPrice,
                Status: order.Status,
                UpdatedAt: DateTimeOffset.UtcNow);

            await eventBus.PublishAsync(ev, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to process order update for {ClientOrderId}", intent.ClientOrderId);
        }
        finally
        {
            sem.Release();
        }
    }
}

/// <summary>
/// Extension to batch an enumerable into fixed-size chunks.
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
