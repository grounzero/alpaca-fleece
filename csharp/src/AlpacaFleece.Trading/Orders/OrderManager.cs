namespace AlpacaFleece.Trading.Orders;

/// <summary>
/// Order manager with SHA-256 idempotent client_order_id generation.
/// Implements 11-step flow: Signal → Risk → Action → Intent Persist → Submit → Event Publish.
/// Persists order intent before submission for crash recovery.
/// Increments circuit breaker on failures; resets on success.
/// </summary>
public sealed class OrderManager(
    IBrokerService broker,
    IRiskManager riskManager,
    IStateRepository stateRepository,
    IEventBus eventBus,
    TradingOptions options,
    ILogger<OrderManager> logger) : IOrderManager
{
    /// <summary>
    /// Submits a signal as an order (persist first, then submit).
    /// 11-step flow:
    /// 1. Determine side (BUY/SELL from signal_type)
    /// 2. Call RiskManager.CheckSignalAsync(signal) - throws if SAFETY/RISK fails
    /// 3. Determine action (ENTER_LONG/EXIT_LONG/ENTER_SHORT/EXIT_SHORT) via PositionTracker
    /// 4. If entry action: check position block, open order block, gate_try_accept
    /// 5. Generate clientOrderId = SHA-256(...) first 16 chars
    /// 6. Check StateRepository.GetOrderIntentAsync(clientOrderId) → if exists, return (duplicate)
    /// 7. StateRepository.SaveOrderIntentAsync(clientOrderId, status="new") [CRASH SAFETY]
    /// 8. If DRY_RUN: log and return
    /// 9. await broker.SubmitOrderAsync(...) [NO RETRY]
    /// 10. StateRepository.UpdateOrderIntentAsync(clientOrderId, status="submitted", alpacaOrderId=...)
    /// 11. EventBus.PublishAsync(new OrderIntentEvent(...))
    /// </summary>
    public async ValueTask<string> SubmitSignalAsync(
        SignalEvent signal,
        int quantity,
        decimal limitPrice,
        CancellationToken ct = default)
    {
        try
        {
            // Step 1: Determine side (already in signal.Side as "BUY" or "SELL")
            var side = signal.Side;

            // Step 2: Call RiskManager.CheckSignalAsync - throws RiskManagerException if SAFETY/RISK fails
            var riskCheckResult = await riskManager.CheckSignalAsync(signal, ct);
            if (!riskCheckResult.AllowsSignal)
            {
                // FILTER tier soft skip - return empty string to indicate rejection
                logger.LogWarning("Signal rejected by risk filter: {reason}", riskCheckResult.Reason);
                return string.Empty;
            }

            // Step 3: Determine action (simplified - assume entry for now)
            // Full implementation would check PositionTracker for reversal scenarios
            var action = DetermineAction(signal.Side);

            // Step 4: Entry action checks (position block, open order block, gate)
            // Simplified for now - full implementation would call gate_try_accept
            if (action.StartsWith("ENTER", StringComparison.OrdinalIgnoreCase))
            {
                // Check position block and gates would go here
            }

            // Step 5: Generate deterministic clientOrderId
            var clientOrderId = OrderIdGenerator.GenerateClientOrderId(
                strategy: "sma_crossover_multi",
                symbol: signal.Symbol,
                timeframe: signal.Timeframe,
                signalTimestamp: signal.SignalTimestamp,
                side: signal.Side.ToLowerInvariant());

            // Step 6: Check for duplicate - if already submitted, return
            var existing = await stateRepository.GetOrderIntentAsync(clientOrderId, ct);
            if (existing is { AlpacaOrderId: not null })
            {
                logger.LogInformation("Order already submitted with client_order_id {id}", clientOrderId);
                return clientOrderId;
            }

            // Step 7: Persist intent BEFORE submission (crash recovery)
            await stateRepository.SaveOrderIntentAsync(
                clientOrderId,
                signal.Symbol,
                signal.Side,
                quantity,
                limitPrice,
                DateTimeOffset.UtcNow,
                ct);

            logger.LogInformation("Order intent persisted: {id}", clientOrderId);

            // Step 8: DRY_RUN check
            if (options.Execution.DryRun)
            {
                logger.LogInformation("DRY_RUN: Order would be submitted: {symbol} {side} {qty} @ {price}",
                    signal.Symbol, signal.Side, quantity, limitPrice);
                return clientOrderId;
            }

            // Step 9: Submit to broker (NO RETRY)
            OrderInfo orderInfo;
            try
            {
                orderInfo = await broker.SubmitOrderAsync(
                    signal.Symbol,
                    signal.Side,
                    quantity,
                    limitPrice,
                    clientOrderId,
                    ct);
            }
            catch (Exception ex)
            {
                // On broker failure: increment circuit breaker
                var currentCount = await stateRepository.GetCircuitBreakerCountAsync(ct);
                await stateRepository.SaveCircuitBreakerCountAsync(currentCount + 1, ct);

                logger.LogError(ex, "Broker submission failed, circuit breaker incremented to {count}",
                    currentCount + 1);

                throw new OrderManagerException("Failed to submit to broker", ex);
            }

            // Step 10: Update with broker's response
            await stateRepository.UpdateOrderIntentAsync(
                clientOrderId,
                orderInfo.AlpacaOrderId,
                orderInfo.Status,
                DateTimeOffset.UtcNow,
                ct);

            // Reset circuit breaker on success
            await stateRepository.SaveCircuitBreakerCountAsync(0, ct);

            logger.LogInformation(
                "Order submitted: client_order_id={client_id}, alpaca_order_id={alpaca_id}",
                clientOrderId, orderInfo.AlpacaOrderId);

            // Step 11: Emit event
            var intentEvent = new OrderIntentEvent(
                Symbol: signal.Symbol,
                Side: signal.Side,
                Quantity: quantity,
                ClientOrderId: clientOrderId,
                CreatedAt: DateTimeOffset.UtcNow);

            await eventBus.PublishAsync(intentEvent, ct);

            return clientOrderId;
        }
        catch (RiskManagerException ex)
        {
            logger.LogWarning(ex, "Risk check failed for {symbol}, order not submitted", signal.Symbol);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit signal order for {symbol}", signal.Symbol);
            throw new OrderManagerException("Failed to submit signal order", ex);
        }
    }

    /// <summary>
    /// Submits an exit order for a symbol (does not go through risk checks).
    /// </summary>
    public async ValueTask SubmitExitAsync(
        string symbol,
        string side,
        int quantity,
        decimal limitPrice,
        CancellationToken ct = default)
    {
        try
        {
            // Exit orders use a unique ID (not deterministic) as they're reactive
            var clientOrderId = $"exit_{symbol}_{Guid.NewGuid().ToString()[..8]}";

            await stateRepository.SaveOrderIntentAsync(
                clientOrderId,
                symbol,
                side,
                quantity,
                limitPrice,
                DateTimeOffset.UtcNow,
                ct);

            // DRY_RUN check
            if (options.Execution.DryRun)
            {
                logger.LogInformation("DRY_RUN: Exit order would be submitted: {symbol} {side} {qty}",
                    symbol, side, quantity);
                return;
            }

            var orderInfo = await broker.SubmitOrderAsync(
                symbol,
                side,
                quantity,
                limitPrice,
                clientOrderId,
                ct);

            await stateRepository.UpdateOrderIntentAsync(
                clientOrderId,
                orderInfo.AlpacaOrderId,
                orderInfo.Status,
                DateTimeOffset.UtcNow,
                ct);

            logger.LogInformation(
                "Exit order submitted: symbol={symbol}, side={side}, qty={qty}",
                symbol, side, quantity);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit exit order for {symbol}", symbol);
            throw new OrderManagerException("Failed to submit exit order", ex);
        }
    }

    /// <summary>
    /// Determines action type from signal side (simplified).
    /// </summary>
    private static string DetermineAction(string side)
    {
        return side switch
        {
            "BUY" => "ENTER_LONG",
            "SELL" => "EXIT_LONG",
            _ => "UNKNOWN"
        };
    }
}
