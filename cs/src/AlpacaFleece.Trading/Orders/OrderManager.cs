namespace AlpacaFleece.Trading.Orders;

/// <summary>
/// Order manager with SHA-256 idempotent client_order_id generation.
/// Implements 11-step flow: Signal → Risk → Action → Entry Gate → Intent Persist → Submit → Event Publish.
/// Persists order intent before submission for crash recovery.
/// Increments circuit breaker on failures; resets on success.
///
/// Entry gating (for ENTER actions only):
///   1. Position block  — rejects if an open DB position already exists for the symbol.
///   2. Pending-order block — rejects if a non-terminal order for same symbol+side exists (different order).
///   3. Per-bar gate — rejects duplicate signals from the same bar (1-minute cooldown via GateTryAcceptAsync).
/// </summary>
public sealed class OrderManager(
    IBrokerService broker,
    IRiskManager riskManager,
    IStateRepository stateRepository,
    IEventBus eventBus,
    TradingOptions options,
    ILogger<OrderManager> logger,
    DrawdownMonitor? drawdownMonitor = null) : IOrderManager
{
    /// <summary>
    /// Submits a signal as an order (persist first, then submit).
    /// Returns the generated clientOrderId, or empty string if the order was gated/filtered.
    /// Pass quantity=0 to auto-size using the dual-formula PositionSizer (equity cap + risk cap).
    /// </summary>
    public async ValueTask<string> SubmitSignalAsync(
        SignalEvent signal,
        decimal quantity,
        decimal limitPrice,
        CancellationToken ct = default)
    {
        try
        {
            // Step 1: Determine side (already in signal.Side as "BUY" or "SELL")
            var side = signal.Side;

            // Crypto symbols allow fractional quantities (no floor to 1; minimum 0.0001).
            var isCrypto = options.Symbols.CryptoSymbols.Contains(
                signal.Symbol, StringComparer.OrdinalIgnoreCase);

            // Step 1b: Get drawdown position multiplier (Warning state reduces sizes)
            var positionMultiplier = drawdownMonitor?.GetPositionMultiplier() ?? 1.0m;

            // Step 1c: Auto-size quantity when caller passes 0 (sentinel = "use PositionSizer")
            if (quantity == 0m)
            {
                var account = await broker.GetAccountAsync(ct);
                quantity = PositionSizer.CalculateQuantity(
                    signal,
                    accountEquity: account.PortfolioValue,
                    maxPositionPct: options.RiskLimits.MaxPositionSizePct,
                    maxRiskPerTradePct: options.RiskLimits.MaxRiskPerTradePct,
                    stopLossPct: options.RiskLimits.StopLossPct,
                    allowFractional: isCrypto);
                logger.LogInformation(
                    "Auto-sized quantity for {symbol}: {qty} (equity={equity:F0}, fractional={isCrypto})",
                    signal.Symbol, quantity, account.PortfolioValue, isCrypto);
            }

            // Step 1d: Apply drawdown position multiplier after sizing (Warning state reduces sizes)
            if (positionMultiplier < 1.0m)
            {
                var originalQty = quantity;
                quantity = isCrypto
                    ? Math.Max(0.0001m, Math.Round(quantity * positionMultiplier, 8))
                    : Math.Max(1m, Math.Floor(quantity * positionMultiplier));
                if (quantity != originalQty)
                {
                    logger.LogInformation(
                        "Drawdown warning: position size reduced from {original} to {qty} ({multiplier:P0}) for {symbol}",
                        originalQty, quantity, positionMultiplier, signal.Symbol);
                }
            }

            // Step 1e: Enforce whole-number quantities when AllowFractionalOrders is disabled.
            // SDK v7.2.0 cannot read back fractional fills; keeping this gate prevents spurious
            // circuit-breaker trips caused by IsFractionalFault detecting a 0-qty filled order.
            if (!options.Execution.AllowFractionalOrders)
            {
                var preFloor = quantity;
                quantity = Math.Max(1m, Math.Floor(quantity));
                if (quantity != preFloor)
                {
                    logger.LogInformation(
                        "AllowFractionalOrders=false: quantity floored from {original} to {qty} for {symbol}",
                        preFloor, quantity, signal.Symbol);
                    if (isCrypto)
                        logger.LogWarning(
                            "AllowFractionalOrders=false is set but {symbol} is a crypto symbol — fractional disabled",
                            signal.Symbol);
                }
            }

            // Step 2: Call RiskManager.CheckSignalAsync - throws RiskManagerException if SAFETY/RISK fails
            var riskCheckResult = await riskManager.CheckSignalAsync(signal, ct);
            if (!riskCheckResult.AllowsSignal)
            {
                logger.LogWarning("Signal rejected by risk filter: {reason}", riskCheckResult.Reason);
                return string.Empty;
            }

            // Step 3: Determine action (simplified long-only: BUY→ENTER_LONG, SELL→EXIT_LONG)
            var action = DetermineAction(signal.Side);

            // Step 4: Generate deterministic clientOrderId before gating so duplicate check works first
            var clientOrderId = OrderIdGenerator.GenerateClientOrderId(
                strategy: "sma_crossover_multi",
                symbol: signal.Symbol,
                timeframe: signal.Timeframe,
                signalTimestamp: signal.SignalTimestamp,
                side: signal.Side.ToLowerInvariant());

            // Step 5: Idempotency check — if this exact order was already submitted, return early
            var existing = await stateRepository.GetOrderIntentAsync(clientOrderId, ct);
            if (existing is { AlpacaOrderId: not null })
            {
                logger.LogInformation("Order already submitted with client_order_id {id}", clientOrderId);
                return clientOrderId;
            }

            // Step 6: Entry gating (ENTER actions only; only for new intents — retries of failed
            // submissions skip the gate so the broker can be reached again)
            if (action.StartsWith("ENTER", StringComparison.OrdinalIgnoreCase) && existing == null)
            {
                // Gate 6a: Position block — don't open a new position if one already exists in DB
                var dbPositions = await stateRepository.GetAllPositionTrackingAsync(ct);
                if (dbPositions.Any(p => p.Symbol == signal.Symbol && p.Quantity > 0))
                {
                    logger.LogInformation(
                        "Position block: already have open position for {symbol}, skipping ENTER",
                        signal.Symbol);
                    return string.Empty;
                }

                // Gate 6b: Pending-order block — don't submit a new BUY if one is already in-flight.
                // Prevents runaway duplicate entries when the strategy fires repeatedly on the same symbol.
                var hasPending = await stateRepository.HasPendingOrderAsync(signal.Symbol, signal.Side, ct);
                if (hasPending)
                {
                    logger.LogInformation(
                        "Pending-order block: non-terminal {side} order already exists for {symbol}, skipping ENTER",
                        signal.Side, signal.Symbol);
                    return string.Empty;
                }

                // Gate 6c: Per-bar gate — accept at most one ENTER per bar timestamp.
                // cooldown=Zero relies purely on the same-bar timestamp check for idempotency;
                // bar polling cadence already enforces rate limiting externally.
                var gateName = $"entry_gate:{signal.Symbol}:{signal.Timeframe}";
                var accepted = await stateRepository.GateTryAcceptAsync(
                    gateName,
                    signal.SignalTimestamp,
                    DateTimeOffset.UtcNow,
                    cooldown: TimeSpan.Zero,
                    ct);

                if (!accepted)
                {
                    logger.LogInformation(
                        "Gate: signal for {symbol} on bar {ts} already accepted this cycle",
                        signal.Symbol, signal.SignalTimestamp);
                    return string.Empty;
                }
            }

            // Step 7: Persist intent BEFORE submission (crash recovery); store ATR seed for fill-time position open
            bool isNewIntent = await stateRepository.SaveOrderIntentAsync(
                clientOrderId,
                signal.Symbol,
                signal.Side,
                quantity,
                limitPrice,
                DateTimeOffset.UtcNow,
                ct,
                atrSeed: signal.Metadata.Atr ?? signal.Metadata.AtrValue);

            if (!isNewIntent)
            {
                // Concurrent caller already saved this intent; they will handle submission
                logger.LogInformation("Order intent {id} already exists (concurrent call), skipping submission", clientOrderId);
                return string.Empty;
            }

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
    /// Submits an exit order for a symbol.
    /// Uses a deterministic clientOrderId based on symbol + UTC date (idempotent per day).
    /// </summary>
    public async ValueTask SubmitExitAsync(
        string symbol,
        string side,
        decimal quantity,
        decimal limitPrice,
        CancellationToken ct = default)
    {
        try
        {
            // Deterministic exit ID: same symbol+side+date will yield the same ID (idempotent per day)
            var nowUtc = DateTimeOffset.UtcNow;
            var dateKey = nowUtc.ToString("yyyyMMdd");
            var dayTs = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, TimeSpan.Zero);
            var clientOrderId = OrderIdGenerator.GenerateClientOrderId(
                strategy: "exit",
                symbol: symbol,
                timeframe: dateKey,
                signalTimestamp: dayTs,
                side: side.ToLowerInvariant());

            // Idempotency check BEFORE persist — if this exit was already submitted to the broker, skip re-submission
            var existingExitIntent = await stateRepository.GetOrderIntentAsync(clientOrderId, ct);
            if (existingExitIntent is { AlpacaOrderId: not null })
            {
                logger.LogInformation(
                    "Exit order already submitted for {symbol} (alpaca_id={alpacaId}), skipping",
                    symbol, existingExitIntent.AlpacaOrderId);
                return;
            }

            // Persist intent AFTER checking for existing order
            // Return value indicates whether this call won the race to persist (true) or another concurrent caller did (false)
            bool isNewExitIntent = await stateRepository.SaveOrderIntentAsync(
                clientOrderId,
                symbol,
                side,
                quantity,
                limitPrice,
                DateTimeOffset.UtcNow,
                ct);

            if (!isNewExitIntent)
            {
                // Concurrent caller already saved this exit intent; they will handle submission
                logger.LogInformation("Exit order {id} already exists (concurrent call), skipping submission", clientOrderId);
                return;
            }

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
    /// Flattens all open broker positions by submitting opposing exit orders.
    /// Long positions → SELL; short positions → BUY.
    /// Per-symbol failures are logged and skipped; returns count of orders submitted.
    /// </summary>
    public async ValueTask<int> FlattenPositionsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<PositionInfo> positions;
        try
        {
            positions = await broker.GetPositionsAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "FlattenPositionsAsync: failed to fetch broker positions");
            throw new OrderManagerException("Failed to fetch positions for flatten", ex);
        }

        if (positions.Count == 0)
        {
            logger.LogInformation("FlattenPositionsAsync: no open positions to flatten");
            return 0;
        }

        logger.LogWarning("FlattenPositionsAsync: flattening {count} positions", positions.Count);

        var submitted = 0;
        foreach (var pos in positions)
        {
            try
            {
                // Long position (qty > 0) → SELL; short (qty < 0) → BUY
                var exitSide = pos.Quantity > 0 ? "SELL" : "BUY";
                var absQty = Math.Abs(pos.Quantity);

                // Deterministic flatten ID: symbol + side + today so the same call is idempotent
                var flattenNow = DateTimeOffset.UtcNow;
                var flattenDateKey = flattenNow.ToString("yyyyMMdd");
                var flattenDayTs = new DateTimeOffset(flattenNow.Year, flattenNow.Month, flattenNow.Day, 0, 0, 0, TimeSpan.Zero);
                var clientOrderId = OrderIdGenerator.GenerateClientOrderId(
                    strategy: "flatten",
                    symbol: pos.Symbol,
                    timeframe: flattenDateKey,
                    signalTimestamp: flattenDayTs,
                    side: exitSide.ToLowerInvariant());

                // Persist intent BEFORE submission (crash recovery idempotency)
                // Use 0m as limitPrice so the broker submits a market order for flatten.
                // Passing pos.CurrentPrice as a limit could cause fill failures when the price moves.
                bool isNewFlattenIntent = await stateRepository.SaveOrderIntentAsync(
                    clientOrderId,
                    pos.Symbol,
                    exitSide,
                    absQty,
                    0m,
                    DateTimeOffset.UtcNow,
                    ct);

                // Check if this intent was already submitted to the broker (has an AlpacaOrderId).
                // On restart after partial crash, isNewFlattenIntent will be false but AlpacaOrderId may or may not be set.
                var existingFlattenIntent = await stateRepository.GetOrderIntentAsync(clientOrderId, ct);
                if (existingFlattenIntent is { AlpacaOrderId: not null })
                {
                    logger.LogInformation(
                        "Flatten order already submitted for {symbol} (alpaca_id={alpacaId}), skipping",
                        pos.Symbol, existingFlattenIntent.AlpacaOrderId);
                    continue;
                }

                if (options.Execution.DryRun)
                {
                    logger.LogInformation(
                        "DRY_RUN: Flatten would submit {side} {qty} {symbol}",
                        exitSide, absQty, pos.Symbol);
                    submitted++;
                    continue;
                }

                logger.LogInformation(
                    "FlattenPositionsAsync: submitting market order for {symbol} {side} {qty}",
                    pos.Symbol, exitSide, absQty);

                var orderInfo = await broker.SubmitOrderAsync(
                    pos.Symbol,
                    exitSide,
                    absQty,
                    0m,   // Market order — limitPrice=0 signals market order to broker
                    clientOrderId,
                    ct);

                await stateRepository.UpdateOrderIntentAsync(
                    clientOrderId,
                    orderInfo.AlpacaOrderId,
                    orderInfo.Status,
                    DateTimeOffset.UtcNow,
                    ct);

                logger.LogInformation(
                    "Flatten order submitted: {symbol} {side} {qty} (alpaca_id={alpacaId})",
                    pos.Symbol, exitSide, absQty, orderInfo.AlpacaOrderId);

                submitted++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                logger.LogError(ex, "FlattenPositionsAsync: failed for {symbol}, skipping", pos.Symbol);
            }
        }

        return submitted;
    }

    /// <summary>
    /// Determines action type from signal side (simplified long-only strategy).
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
