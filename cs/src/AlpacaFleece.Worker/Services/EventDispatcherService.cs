namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Event dispatcher service: reads from event bus and dispatches to handlers.
/// Priority drain: ExitSignalEvent (never dropped) → OrderUpdateEvent → SignalEvent → Others.
/// Signal flow: BarEvent → BarsHandler (persistence) → Strategy.OnBarAsync() → SignalEvent
///           → RiskManager.CheckSignalAsync() → OrderManager.SubmitSignalAsync()
/// </summary>
public sealed class EventDispatcherService(
    IEventBus eventBus,
    IServiceProvider serviceProvider,
    IOptions<TradingOptions> tradingOptions,
    ILogger<EventDispatcherService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Event dispatcher started");

        try
        {
            try
            {
                var dataHandler = serviceProvider.GetRequiredService<IDataHandler>();
                dataHandler.Initialise();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to initialise DataHandler at startup");
            }

            await eventBus.DispatchAsync(HandleEventAsync, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("Event dispatcher stopped");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Event dispatcher encountered unexpected error");
            throw;
        }
    }

    private async ValueTask HandleEventAsync(IEvent @event)
    {
        try
        {
            // Priority handling: ExitSignalEvent first (never dropped)
            switch (@event)
            {
                case ExitSignalEvent exitEvent:
                    await HandleExitSignalEventAsync(exitEvent);
                    return;

                case OrderUpdateEvent updateEvent:
                    await HandleOrderUpdateEventAsync(updateEvent);
                    return;

                case SignalEvent signalEvent:
                    await HandleSignalEventAsync(signalEvent);
                    return;

                case BarEvent barEvent:
                    await HandleBarEventAsync(barEvent);
                    return;

                case OrderIntentEvent orderEvent:
                    await HandleOrderIntentEventAsync(orderEvent);
                    return;

                default:
                    logger.LogDebug("Dispatching event of type {type}", @event.GetType().Name);
                    return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error handling event {type}", @event.GetType().Name);
            // Don't rethrow - continue processing other events
        }
    }

    /// <summary>
    /// Handles exit signal events (highest priority, never dropped).
    /// Routes to OrderManager.SubmitExitAsync().
    /// </summary>
    private async ValueTask HandleExitSignalEventAsync(ExitSignalEvent exitEvent)
    {
        logger.LogInformation(
            "Handling ExitSignalEvent: {symbol} {reason}",
            exitEvent.Symbol, exitEvent.ExitReason);

        try
        {
            // Resolve scoped services from dedicated scope (IOrderManager is scoped)
            using var scope = serviceProvider.CreateScope();
            var orderManager = scope.ServiceProvider.GetRequiredService<IOrderManager>();
            var positionTracker = scope.ServiceProvider.GetRequiredService<IPositionTracker>();

            // Long-only strategy: exits are always sells.
            var exitSide = "SELL";

            // Quantity comes from the in-memory position tracker (set when the fill was received).
            var posData = positionTracker.GetPosition(exitEvent.Symbol);
            var exitQty = posData?.CurrentQuantity ?? 0m;
            if (exitQty <= 0m)
            {
                logger.LogWarning(
                    "No open position found for {symbol} during exit — skipping",
                    exitEvent.Symbol);
                return;
            }

            await orderManager.SubmitExitAsync(
                symbol: exitEvent.Symbol,
                side: exitSide,
                quantity: exitQty,
                limitPrice: exitEvent.ExitPrice,
                ct: CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle exit signal for {symbol}", exitEvent.Symbol);
            // Continue despite errors
        }
    }

    /// <summary>
    /// Handles order update events: opens/closes positions on fill, clears PendingExit.
    /// </summary>
    private async ValueTask HandleOrderUpdateEventAsync(OrderUpdateEvent updateEvent)
    {
        logger.LogInformation(
            "Handling OrderUpdateEvent: {symbol} {status} {side}",
            updateEvent.Symbol, updateEvent.Status, updateEvent.Side);

        try
        {
            // Resolve scoped services from dedicated scope
            using var scope = serviceProvider.CreateScope();
            var positionTracker = scope.ServiceProvider.GetRequiredService<IPositionTracker>();
            var exitManager = scope.ServiceProvider.GetRequiredService<ExitManager>();

            if (updateEvent.Status == OrderState.Filled)
            {
                var stateRepo = scope.ServiceProvider.GetRequiredService<IStateRepository>();

                if (string.Equals(updateEvent.Side, "BUY", StringComparison.OrdinalIgnoreCase))
                {
                    // Look up the stored ATR seed from the original signal intent
                    var intent = await stateRepo.GetOrderIntentAsync(updateEvent.ClientOrderId);
                    var atr = intent?.AtrSeed ?? 0m;

                    if (atr <= 0m)
                    {
                        // AtrSeed missing (pre-migration intent or unexpected path). ExitManager
                        // skips positions with AtrValue ≤ 0, leaving this position unprotected.
                        // Log an error so the operator can investigate; the fill is still recorded.
                        logger.LogError(
                            "BUY fill for {Symbol} has no ATR seed (intent={ClientOrderId}). " +
                            "Position will open with AtrValue=0 and will be skipped by ExitManager — " +
                            "manual exit monitoring required.",
                            updateEvent.Symbol, updateEvent.ClientOrderId);
                    }

                    await positionTracker.OpenPositionAsync(
                        updateEvent.Symbol,
                        updateEvent.FilledQuantity,
                        updateEvent.AverageFilledPrice,
                        atr);
                    await stateRepo.IncrementDailyTradeCountAsync();
                }
                else
                {
                    // Capture entry price BEFORE closing to compute realised PnL
                    var pos = positionTracker.GetPosition(updateEvent.Symbol);
                    if (pos != null && updateEvent.FilledQuantity > 0 && updateEvent.AverageFilledPrice > 0)
                    {
                        var pnl = (updateEvent.AverageFilledPrice - pos.EntryPrice) * updateEvent.FilledQuantity;
                        await stateRepo.AddDailyRealizedPnlAsync(pnl);
                    }

                    // SELL fill — exit; zero out the position
                    await positionTracker.ClosePositionAsync(updateEvent.Symbol);
                    await stateRepo.IncrementDailyTradeCountAsync();
                }
            }
            else if (updateEvent.Status == OrderState.PartiallyFilled)
            {
                // Partial fills: update in-memory + DB position to reflect cumulative filled qty.
                // Uses the intent's quantity to derive the remaining qty for SELL orders (idempotent).
                var stateRepo = scope.ServiceProvider.GetRequiredService<IStateRepository>();

                if (string.Equals(updateEvent.Side, "BUY", StringComparison.OrdinalIgnoreCase))
                {
                    var existingPos = positionTracker.GetPosition(updateEvent.Symbol);
                    if (existingPos == null)
                    {
                        // First partial fill: open position with ATR seed (same path as Filled)
                        var intent = await stateRepo.GetOrderIntentAsync(updateEvent.ClientOrderId);
                        var atr = intent?.AtrSeed ?? 0m;
                        if (atr <= 0m)
                            logger.LogError(
                                "BUY partial fill for {Symbol} has no ATR seed (intent={ClientOrderId}). " +
                                "Position will open with AtrValue=0 and will be skipped by ExitManager.",
                                updateEvent.Symbol, updateEvent.ClientOrderId);
                        await positionTracker.OpenPositionAsync(
                            updateEvent.Symbol,
                            updateEvent.FilledQuantity,
                            updateEvent.AverageFilledPrice,
                            atr);
                    }
                    else
                    {
                        // Subsequent partial fill: scale up to new cumulative filled qty
                        await positionTracker.UpdateQuantityAsync(
                            updateEvent.Symbol,
                            updateEvent.FilledQuantity,
                            updateEvent.AverageFilledPrice);
                    }
                }
                else
                {
                    // SELL partial fill: reduce position by cumulative filled qty of the SELL order.
                    // intent.Quantity = original position qty submitted for exit; FilledQuantity is cumulative.
                    var intent = await stateRepo.GetOrderIntentAsync(updateEvent.ClientOrderId);
                    var originalSellQty = intent?.Quantity ?? 0m;
                    var pos = positionTracker.GetPosition(updateEvent.Symbol);

                    if (pos != null)
                    {
                        var remainingQty = originalSellQty - updateEvent.FilledQuantity;
                        if (remainingQty <= 0m)
                            await positionTracker.ClosePositionAsync(updateEvent.Symbol);
                        else
                            // Preserve the original entry price — for a partial exit the cost
                            // basis of the remaining position is unchanged; only qty is reduced.
                            await positionTracker.UpdateQuantityAsync(
                                updateEvent.Symbol, remainingQty, pos.EntryPrice);
                    }
                }
            }
            else if (updateEvent.Status is OrderState.Canceled or OrderState.Expired or OrderState.Rejected)
            {
                // Terminal failure states — clear PendingExit so ExitManager can retry
                await exitManager.HandleOrderUpdateAsync(updateEvent, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle order update for {symbol}", updateEvent.Symbol);
        }
    }

    /// <summary>
    /// Handles signal events: OrderManager.SubmitSignalAsync (risk check is authoritative in OrderManager).
    /// H-2: The risk check here was a duplicate — removed. OrderManager.SubmitSignalAsync calls
    ///      RiskManager.CheckSignalAsync internally and throws RiskManagerException on SAFETY/RISK failure.
    /// R-5: OrderManager is scoped; resolve from a new scope to avoid consuming a root-scoped instance.
    /// </summary>
    private async ValueTask HandleSignalEventAsync(SignalEvent signalEvent)
    {
        logger.LogInformation(
            "Handling SignalEvent: {symbol} {side}",
            signalEvent.Symbol, signalEvent.Side);

        try
        {
            // R-5: IOrderManager is scoped — resolve from a dedicated scope to avoid root-scope lifetime issues.
            using var scope = serviceProvider.CreateScope();
            var orderManager = scope.ServiceProvider.GetRequiredService<IOrderManager>();

            // Pass 0m as sentinel so OrderManager auto-sizes using the dual-formula PositionSizer
            // (equity cap + risk cap, with fractional support for crypto).
            var qty = 0m;

            // Determine limit price: "Market" passes 0m so the broker submits a market order;
            // "AggressiveLimit" uses the bar-close price from the signal (may be stale).
            var limitPrice = tradingOptions.Value.Execution.EntryOrderType
                .Equals("Market", StringComparison.OrdinalIgnoreCase)
                ? 0m
                : signalEvent.Metadata.CurrentPrice;

            // Submit order — risk check is performed inside SubmitSignalAsync
            var clientOrderId = await orderManager.SubmitSignalAsync(
                signalEvent,
                qty,
                limitPrice: limitPrice,
                ct: CancellationToken.None);

            if (!string.IsNullOrEmpty(clientOrderId))
            {
                logger.LogInformation("Signal submitted as order: {clientOrderId}", clientOrderId);
            }
        }
        catch (RiskManagerException ex)
        {
            logger.LogWarning(ex, "Signal rejected by risk manager");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle signal for {symbol}", signalEvent.Symbol);
        }
    }

    /// <summary>
    /// Handles bar events: persists to BarsHandler and forwards to Strategy for signal generation.
    /// Signal flow: BarEvent → BarsHandler (persistence) → Strategy.OnBarAsync() → SignalEvent
    /// </summary>
    private async ValueTask HandleBarEventAsync(BarEvent barEvent)
    {
        logger.LogInformation(
            "Handling BarEvent: {symbol} {timeframe} {timestamp}",
            barEvent.Symbol, barEvent.Timeframe, barEvent.Timestamp);

        try
        {
            // Persist bar
            var barsHandler = serviceProvider.GetRequiredService<BarsHandler>();
            await barsHandler.HandleBarEventAsync(barEvent, CancellationToken.None);
            logger.LogDebug("Bar persisted for {symbol}", barEvent.Symbol);

            // Forward to strategy for signal generation (strategy handles readiness internally)
            using var scope = serviceProvider.CreateScope();
            var strategy = scope.ServiceProvider.GetRequiredService<IStrategy>();
            logger.LogDebug("Forwarding bar to strategy for {symbol}", barEvent.Symbol);
            await strategy.OnBarAsync(barEvent, CancellationToken.None);
        }
        catch (Exception ex)
        {
            if (ex is BarsHandlerException)
            {
                // BarsHandler already logged this error at the appropriate level; avoid double-logging.
                logger.LogDebug(ex, "BarsHandlerException encountered while handling bar for {symbol}", barEvent.Symbol);
            }
            else
            {
                logger.LogError(ex, "Failed to handle bar for {symbol}", barEvent.Symbol);
            }
        }
    }

    /// <summary>
    /// Handles order intent events (logging, auditing).
    /// </summary>
    private async ValueTask HandleOrderIntentEventAsync(OrderIntentEvent orderEvent)
    {
        logger.LogInformation(
            "Handling OrderIntentEvent: {symbol} {side} {qty}",
            orderEvent.Symbol, orderEvent.Side, orderEvent.Quantity);

        await Task.CompletedTask;
    }
}
