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
            var orderManager = serviceProvider.GetRequiredService<IOrderManager>();
            var positionTracker = serviceProvider.GetRequiredService<IPositionTracker>();

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
            var positionTracker = serviceProvider.GetRequiredService<IPositionTracker>();
            var exitManager = serviceProvider.GetRequiredService<ExitManager>();

            if (updateEvent.Status == OrderState.Filled)
            {
                var stateRepo = serviceProvider.GetRequiredService<IStateRepository>();

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
    /// Handles signal events: RiskManager.CheckSignalAsync → OrderManager.SubmitSignalAsync.
    /// </summary>
    private async ValueTask HandleSignalEventAsync(SignalEvent signalEvent)
    {
        logger.LogInformation(
            "Handling SignalEvent: {symbol} {side}",
            signalEvent.Symbol, signalEvent.Side);

        try
        {
            var riskManager = serviceProvider.GetRequiredService<IRiskManager>();
            var orderManager = serviceProvider.GetRequiredService<IOrderManager>();

            // Risk check (throws RiskManagerException on SAFETY/RISK failure, soft skip on FILTER)
            var riskCheckResult = await riskManager.CheckSignalAsync(
                signalEvent,
                CancellationToken.None);

            if (!riskCheckResult.AllowsSignal)
            {
                logger.LogWarning("Signal rejected by risk filter: {reason}", riskCheckResult.Reason);
                return;
            }

            // Pass 0m as sentinel so OrderManager auto-sizes using the dual-formula PositionSizer
            // (equity cap + risk cap, with fractional support for crypto).
            var qty = 0m;

            // Submit order
            var clientOrderId = await orderManager.SubmitSignalAsync(
                signalEvent,
                qty,
                limitPrice: signalEvent.Metadata.CurrentPrice, // Market order proxy
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
            logger.LogInformation("Bar persisted for {symbol}", barEvent.Symbol);

            // Forward to strategy for signal generation (strategy handles readiness internally)
            using var scope = serviceProvider.CreateScope();
            var strategy = scope.ServiceProvider.GetRequiredService<IStrategy>();
            logger.LogInformation("Forwarding bar to strategy for {symbol}", barEvent.Symbol);
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
