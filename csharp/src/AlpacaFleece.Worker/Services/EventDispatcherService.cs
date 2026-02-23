namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Event dispatcher service: reads from event bus and dispatches to handlers.
/// Priority drain: ExitSignalEvent (never dropped) → OrderUpdateEvent → SignalEvent → Others.
/// Signal flow: BarEvent → DataHandler.OnBar() → Strategy.OnBar() → SignalEvent
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

            // Exit side depends on what position we're closing
            // For now, assume we're selling (closing long position)
            var exitSide = "SELL";

            await orderManager.SubmitExitAsync(
                symbol: exitEvent.Symbol,
                side: exitSide,
                quantity: 100, // Default quantity, would come from position tracker in real flow
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
    /// Handles order update events (updates position tracker, etc.).
    /// </summary>
    private async ValueTask HandleOrderUpdateEventAsync(OrderUpdateEvent updateEvent)
    {
        logger.LogInformation(
            "Handling OrderUpdateEvent: {symbol} {status}",
            updateEvent.Symbol, updateEvent.Status);

        try
        {
            var positionTracker = serviceProvider.GetRequiredService<PositionTracker>();

            // Update position based on order status
            if (updateEvent.Status == OrderState.Filled)
            {
                // Update position tracker with filled quantity and price
                positionTracker.UpdateTrailingStop(updateEvent.Symbol, updateEvent.AverageFilledPrice);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle order update for {symbol}", updateEvent.Symbol);
        }

        await Task.CompletedTask;
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

            // Calculate position size
            var account = await serviceProvider
                .GetRequiredService<IBrokerService>()
                .GetAccountAsync(CancellationToken.None);

            var qty = (int)PositionSizer.CalculateQuantity(signalEvent, account.PortfolioValue);

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
    /// Handles bar events: routes to DataHandler for storage and history management.
    /// </summary>
    private async ValueTask HandleBarEventAsync(BarEvent barEvent)
    {
        logger.LogDebug(
            "Handling BarEvent: {symbol} {timeframe} {timestamp}",
            barEvent.Symbol, barEvent.Timeframe, barEvent.Timestamp);

        try
        {
            var dataHandler = serviceProvider.GetRequiredService<IDataHandler>();
            // DataHandler.Initialise() sets up subscriptions; actual bar processing happens via event bus
            dataHandler.Initialise();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to handle bar for {symbol}", barEvent.Symbol);
        }

        await Task.CompletedTask;
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
