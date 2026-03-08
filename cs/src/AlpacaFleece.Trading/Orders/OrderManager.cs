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
    DrawdownMonitor? drawdownMonitor = null,
    VolatilityRegimeDetector? volatilityRegimeDetector = null,
    IPositionTracker? positionTracker = null) : IOrderManager
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _submissionLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<OrderState> ActiveOrderStates =
    [
        OrderState.PendingNew,
        OrderState.Accepted,
        OrderState.PartiallyFilled,
        OrderState.PendingCancel,
        OrderState.PendingReplace,
    ];

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

            // Step 1b: Determine action (simplified long-only: BUY→ENTER_LONG, SELL→EXIT_LONG)
            var action = DetermineAction(signal.Side);

            // Crypto symbols allow fractional quantities (no floor to 1; minimum 0.0001).
            var isCrypto = options.Symbols.CryptoSymbols.Contains(
                signal.Symbol, StringComparer.OrdinalIgnoreCase);

            // Step 1c: Get drawdown position multiplier (Warning state reduces sizes)
            var positionMultiplier = drawdownMonitor?.GetPositionMultiplier() ?? 1.0m;
            var autoSized = false;
            var accountEquity = 0m;

            // Step 1d: For long-only EXIT_LONG actions, enforce quantity from open position.
            // This prevents strategy SELL signals from being auto-sized like entries and
            // accidentally opening short positions after flattening.
            if (action == "EXIT_LONG")
            {
                var openQty = await GetOpenLongQuantityAsync(signal.Symbol, ct);
                if (openQty <= 0m)
                {
                    logger.LogInformation(
                        "EXIT_LONG skip for {symbol}: no open long position",
                        signal.Symbol);
                    return string.Empty;
                }

                if (quantity <= 0m || quantity > openQty)
                {
                    if (quantity > openQty)
                    {
                        logger.LogInformation(
                            "EXIT_LONG quantity clamped for {symbol}: requested={requested} open={open}",
                            signal.Symbol, quantity, openQty);
                    }

                    quantity = openQty;
                }
            }

            // Step 1e: Auto-size quantity when caller passes 0 (sentinel = "use PositionSizer").
            // Applies to entries only; exits use the tracked open quantity above.
            if (quantity == 0m && action == "ENTER_LONG")
            {
                var account = await broker.GetAccountAsync(ct);
                accountEquity = account.PortfolioValue;
                autoSized = true;
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

            // Step 1f: Volatility regime sizing multiplier (entries only).
            // Applied before drawdown scaling so drawdown remains the final safety override.
            if (volatilityRegimeDetector is { Enabled: true } &&
                action == "ENTER_LONG")
            {
                var regime = await volatilityRegimeDetector.GetRegimeAsync(signal.Symbol, ct);
                var preVol = quantity;
                quantity = isCrypto
                    ? Math.Max(0.0001m, Math.Round(quantity * regime.PositionMultiplier, 8))
                    : Math.Max(1m, Math.Floor(quantity * regime.PositionMultiplier));

                logger.LogInformation(
                    "Volatility sizing for {symbol}: regime={regime} vol={vol:F6} bars={bars} " +
                    "multiplier={mult:F2} qty={before}->{after}",
                    signal.Symbol, regime.Regime, regime.RealisedVolatility, regime.BarsInRegime,
                    regime.PositionMultiplier, preVol, quantity);
            }

            // Step 1g: Apply drawdown position multiplier after sizing (Warning state reduces sizes).
            // Entry-only by design: drawdown scaling should not reduce exit order size.
            if (positionMultiplier < 1.0m && action == "ENTER_LONG")
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

            // Step 1h: Re-apply position-size cap for auto-sized BUYs after adaptive multipliers.
            // PositionSizer enforces this cap at baseline sizing, but volatility multipliers > 1.0
            // can otherwise push final quantity above MaxPositionSizePct.
            if (autoSized && action == "ENTER_LONG")
            {
                var priceForCap = signal.Metadata.CurrentPrice > 0m ? signal.Metadata.CurrentPrice : limitPrice;
                if (priceForCap > 0m)
                {
                    var maxNotional = accountEquity * options.RiskLimits.MaxPositionSizePct;
                    var rawCapQty = maxNotional / priceForCap;
                    var capQty = isCrypto
                        ? Math.Floor(rawCapQty * 100_000_000m) / 100_000_000m
                        : Math.Floor(rawCapQty);

                    if (capQty <= 0m)
                    {
                        logger.LogWarning(
                            "Auto-sized quantity rejected for {symbol}: cap quantity <= 0 (equity={equity:F2}, maxPct={pct:P2}, price={price:F4})",
                            signal.Symbol, accountEquity, options.RiskLimits.MaxPositionSizePct, priceForCap);
                        return string.Empty;
                    }

                    if (quantity > capQty)
                    {
                        var preCapQty = quantity;
                        quantity = capQty;
                        logger.LogInformation(
                            "Auto-sized quantity capped for {symbol}: {before} -> {after} (maxNotional={maxNotional:F2}, price={price:F4})",
                            signal.Symbol, preCapQty, quantity, maxNotional, priceForCap);
                    }
                }
            }

            // Step 1i: Enforce whole-number quantities when AllowFractionalOrders is disabled.
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
            // Note: SaveOrderIntentAsync is idempotent on same clientOrderId (signal can be retried).
            // The intent may already exist from a previous failed submission attempt.
            await stateRepository.SaveOrderIntentAsync(
                clientOrderId,
                signal.Symbol,
                signal.Side,
                quantity,
                limitPrice,
                DateTimeOffset.UtcNow,
                ct,
                atrSeed: signal.Metadata.Atr ?? signal.Metadata.AtrValue);

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
    /// Recovers a pending unsent intent when present; otherwise creates a fresh clientOrderId.
    /// </summary>
    public async ValueTask SubmitExitAsync(
        string symbol,
        string side,
        decimal quantity,
        decimal limitPrice,
        CancellationToken ct = default)
    {
        var lockKey = $"{symbol}:{side}";
        var gate = _submissionLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var (clientOrderId, shouldSubmit) = await ResolveExitIntentAsync(
                strategy: "exit", symbol, side, quantity, limitPrice, ct);
            if (!shouldSubmit)
                return;

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
        finally
        {
            gate.Release();
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
            var lockKey = $"{pos.Symbol}:{(pos.Quantity > 0 ? "SELL" : "BUY")}";
            var gate = _submissionLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(ct);
            try
            {
                // Long position (qty > 0) → SELL; short (qty < 0) → BUY
                var exitSide = pos.Quantity > 0 ? "SELL" : "BUY";
                var absQty = Math.Abs(pos.Quantity);

                var (clientOrderId, shouldSubmit) = await ResolveExitIntentAsync(
                    strategy: "flatten", pos.Symbol, exitSide, absQty, 0m, ct);
                if (!shouldSubmit)
                    continue;

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
            finally
            {
                gate.Release();
            }
        }

        return submitted;
    }

    private async ValueTask<(string ClientOrderId, bool ShouldSubmit)> ResolveExitIntentAsync(
        string strategy,
        string symbol,
        string side,
        decimal quantity,
        decimal limitPrice,
        CancellationToken ct)
    {
        var intents = await stateRepository.GetNonTerminalOrderIntentsAsync(ct);
        var activeIntent = intents
            .Where(i => i.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase) &&
                        i.Side.Equals(side, StringComparison.OrdinalIgnoreCase) &&
                        ActiveOrderStates.Contains(i.Status))
            .OrderByDescending(i => i.CreatedAt)
            .FirstOrDefault();

        if (activeIntent is { AlpacaOrderId: not null })
        {
            logger.LogInformation(
                "{strategy} order already active for {symbol} (alpaca_id={alpacaId}), skipping",
                strategy, symbol, activeIntent.AlpacaOrderId);
            return (activeIntent.ClientOrderId, false);
        }

        if (activeIntent is { AlpacaOrderId: null })
        {
            logger.LogInformation(
                "{strategy} intent {id} exists but not submitted yet for {symbol} (crash recovery)",
                strategy, activeIntent.ClientOrderId, symbol);
            return (activeIntent.ClientOrderId, true);
        }

        const int maxCreateAttempts = 3;
        for (var attempt = 1; attempt <= maxCreateAttempts; attempt++)
        {
            var nowUtc = DateTimeOffset.UtcNow;
            var sequenceKey = nowUtc.ToUnixTimeMilliseconds().ToString();
            var clientOrderId = OrderIdGenerator.GenerateClientOrderId(
                strategy: strategy,
                symbol: symbol,
                timeframe: sequenceKey,
                signalTimestamp: nowUtc,
                side: side.ToLowerInvariant());

            var saved = await stateRepository.SaveOrderIntentAsync(
                clientOrderId,
                symbol,
                side,
                quantity,
                limitPrice,
                nowUtc,
                ct);

            if (saved)
            {
                logger.LogInformation(
                    "Created new {strategy} intent {id} for {symbol}",
                    strategy, clientOrderId, symbol);
                return (clientOrderId, true);
            }

            var existing = await stateRepository.GetOrderIntentAsync(clientOrderId, ct);
            if (existing is { AlpacaOrderId: not null } && ActiveOrderStates.Contains(existing.Status))
            {
                logger.LogInformation(
                    "{strategy} order already active for {symbol} (alpaca_id={alpacaId}), skipping",
                    strategy, symbol, existing.AlpacaOrderId);
                return (existing.ClientOrderId, false);
            }

            if (existing is { AlpacaOrderId: null } && ActiveOrderStates.Contains(existing.Status))
            {
                logger.LogInformation(
                    "{strategy} intent {id} exists but not submitted yet for {symbol} (crash recovery, duplicate id)",
                    strategy, existing.ClientOrderId, symbol);
                return (existing.ClientOrderId, true);
            }
        }

        logger.LogWarning(
            "Could not create unique {strategy} intent for {symbol} after retries; skipping submit",
            strategy, symbol);
        return (string.Empty, false);
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

    private async ValueTask<decimal> GetOpenLongQuantityAsync(string symbol, CancellationToken ct)
    {
        if (positionTracker != null)
        {
            var tracked = positionTracker.GetPosition(symbol);
            if (tracked?.CurrentQuantity > 0m)
                return tracked.CurrentQuantity;
            return 0m;
        }

        // Backward-compatible fallback for test fixtures and legacy wiring.
        var rows = await stateRepository.GetAllPositionTrackingAsync(ct);
        var row = rows.FirstOrDefault(p => string.Equals(p.Symbol, symbol, StringComparison.OrdinalIgnoreCase));
        return row.Quantity > 0m ? row.Quantity : 0m;
    }
}
