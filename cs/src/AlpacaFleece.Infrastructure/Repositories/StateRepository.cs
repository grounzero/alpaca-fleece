namespace AlpacaFleece.Infrastructure.Repositories;

/// <summary>
/// State repository implementation with atomic gate logic and KV store.
/// </summary>
public sealed class StateRepository(
    IDbContextFactory<TradingDbContext> dbContextFactory,
    ILogger<StateRepository> logger) : IStateRepository
{
    private IDbContextFactory<TradingDbContext> DbFactory => dbContextFactory;
    /// <summary>
    /// Gets a KV pair from bot_state.
    /// </summary>
    public async ValueTask<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var entity = await dbContext.BotState
                .FirstOrDefaultAsync(x => x.Key == key, ct);

            return entity?.Value;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get state for key {key}", key);
            throw new StateRepositoryException($"Failed to get state for key {key}", ex);
        }
    }

    /// <summary>
    /// Sets a KV pair in bot_state (upsert).
    /// </summary>
    public async ValueTask SetStateAsync(string key, string value, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var entity = await dbContext.BotState
                .FirstOrDefaultAsync(x => x.Key == key, ct);

            if (entity == null)
            {
                dbContext.BotState.Add(new BotStateEntity
                {
                    Key = key,
                    Value = value,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                entity.Value = value;
                entity.UpdatedAt = DateTimeOffset.UtcNow;
            }

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to set state for key {key}", key);
            throw new StateRepositoryException($"Failed to set state for key {key}", ex);
        }
    }

    /// <summary>
    /// Atomically checks and accepts a gate with Serializable isolation.
    /// Returns true if accepted, false if rejected (same-bar dedupe or cooldown).
    /// </summary>
    public async ValueTask<bool> GateTryAcceptAsync(
        string gateName,
        DateTimeOffset barTimestamp,
        DateTimeOffset nowUtc,
        TimeSpan cooldown,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            try
            {
                var gate = await dbContext.SignalGates
                    .FirstOrDefaultAsync(x => x.GateName == gateName, ct);

                // Same-bar rejection
                if (gate?.LastAcceptedBarTs == barTimestamp)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogDebug("Gate {gateName} rejected: same-bar duplicate", gateName);
                    return false;
                }

                // Cooldown check
                if (gate?.LastAcceptedTs != null)
                {
                    var timeSinceLastAccept = nowUtc - gate.LastAcceptedTs.Value;
                    if (timeSinceLastAccept < cooldown)
                    {
                        await tx.RollbackAsync(ct);
                        logger.LogDebug(
                            "Gate {gateName} rejected: cooldown active ({elapsed} < {cooldown})",
                            gateName, timeSinceLastAccept, cooldown);
                        return false;
                    }
                }

                // Accept: monotonic update (only write if nowUtc >= existing)
                if (gate == null)
                {
                    dbContext.SignalGates.Add(new SignalGateEntity
                    {
                        GateName = gateName,
                        LastAcceptedBarTs = barTimestamp,
                        LastAcceptedTs = nowUtc,
                        UpdatedAt = nowUtc
                    });
                }
                else
                {
                    if (nowUtc >= gate.LastAcceptedTs)
                    {
                        gate.LastAcceptedBarTs = barTimestamp;
                        gate.LastAcceptedTs = nowUtc;
                        gate.UpdatedAt = nowUtc;
                    }
                }

                await dbContext.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                logger.LogDebug("Gate {gateName} accepted", gateName);
                return true;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check gate {gateName}", gateName);
            throw new StateRepositoryException($"Failed to check gate {gateName}", ex);
        }
    }

    /// <summary>
    /// Saves an order intent to persistence.
    /// </summary>
    public async ValueTask SaveOrderIntentAsync(
        string clientOrderId,
        string symbol,
        string side,
        decimal quantity,
        decimal limitPrice,
        DateTimeOffset createdAt,
        CancellationToken ct = default,
        decimal? atrSeed = null)
    {
        try
        {
            // Idempotency: return silently if already exists
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var existing = await dbContext.OrderIntents
                .FirstOrDefaultAsync(x => x.ClientOrderId == clientOrderId, ct);
            if (existing != null)
                return;

            dbContext.OrderIntents.Add(new OrderIntentEntity
            {
                ClientOrderId = clientOrderId,
                Symbol = symbol,
                Side = side,
                Quantity = quantity,
                LimitPrice = limitPrice,
                Status = nameof(OrderState.PendingNew),
                CreatedAt = createdAt,
                AtrSeed = atrSeed
            });

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // No DbContext state kept between calls when using factory; nothing to clear here
            logger.LogError(ex, "Failed to save order intent {clientOrderId}", clientOrderId);
            throw new StateRepositoryException($"Failed to save order intent", ex);
        }
    }

    /// <summary>
    /// Updates an existing order intent status.
    /// </summary>
    public async ValueTask UpdateOrderIntentAsync(
        string clientOrderId,
        string alpacaOrderId,
        OrderState status,
        DateTimeOffset updatedAt,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var intent = await dbContext.OrderIntents
                .FirstOrDefaultAsync(x => x.ClientOrderId == clientOrderId, ct);

            if (intent == null)
                throw new StateRepositoryException($"Order intent {clientOrderId} not found");

            intent.AlpacaOrderId = alpacaOrderId;
            intent.Status = status.ToString();
            intent.UpdatedAt = updatedAt;

            // O-2: Stamp the first-seen transition timestamp (??= preserves the earliest timestamp).
            intent.AcceptedAt ??= status == OrderState.Accepted ? updatedAt : null;
            intent.FilledAt ??= status == OrderState.Filled ? updatedAt : null;
            intent.CanceledAt ??= status is OrderState.Canceled or OrderState.Expired or OrderState.Rejected
                ? updatedAt : null;

            await dbContext.SaveChangesAsync(ct);
        }
        catch (StateRepositoryException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update order intent {clientOrderId}", clientOrderId);
            throw new StateRepositoryException($"Failed to update order intent", ex);
        }
    }

    /// <summary>
    /// Retrieves an order intent by client order ID.
    /// </summary>
    public async ValueTask<OrderIntentDto?> GetOrderIntentAsync(string clientOrderId, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var intent = await dbContext.OrderIntents
                .FirstOrDefaultAsync(x => x.ClientOrderId == clientOrderId, ct);

            if (intent == null)
                return null;

            return new OrderIntentDto(
                ClientOrderId: intent.ClientOrderId,
                AlpacaOrderId: intent.AlpacaOrderId,
                Symbol: intent.Symbol,
                Side: intent.Side,
                Quantity: intent.Quantity,
                LimitPrice: intent.LimitPrice,
                Status: Enum.Parse<OrderState>(intent.Status),
                CreatedAt: intent.CreatedAt,
                UpdatedAt: intent.UpdatedAt,
                AtrSeed: intent.AtrSeed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get order intent {clientOrderId}", clientOrderId);
            throw new StateRepositoryException($"Failed to get order intent", ex);
        }
    }

    /// <summary>
    /// Idempotently inserts a fill using unique constraint on (alpaca_order_id, fill_dedupe_key).
    /// </summary>
    public async ValueTask InsertFillIdempotentAsync(
        string alpacaOrderId,
        string clientOrderId,
        decimal filledQuantity,
        decimal filledPrice,
        string dedupeKey,
        DateTimeOffset filledAt,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var existing = await dbContext.Fills
                .FirstOrDefaultAsync(
                    x => x.AlpacaOrderId == alpacaOrderId && x.FillDedupeKey == dedupeKey,
                    ct);

            if (existing != null)
            {
                logger.LogDebug(
                    "Fill already exists for order {alpacaOrderId} with dedupe key {key}",
                    alpacaOrderId, dedupeKey);
                return;
            }

            dbContext.Fills.Add(new FillEntity
            {
                AlpacaOrderId = alpacaOrderId,
                ClientOrderId = clientOrderId,
                FilledQuantity = filledQuantity,
                FilledPrice = filledPrice,
                FillDedupeKey = dedupeKey,
                FilledAt = filledAt
            });

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to insert fill for order {alpacaOrderId}", alpacaOrderId);
            throw new StateRepositoryException($"Failed to insert fill", ex);
        }
    }

    /// <summary>
    /// Gets exponential backoff seconds for exit attempts on a symbol.
    /// Formula: 2^(attemptCount - 1) capped at 300 seconds (5 minutes).
    /// </summary>
    public async ValueTask<int> GetExitBackoffSecondsAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var attempt = await dbContext.ExitAttempts
                .FirstOrDefaultAsync(x => x.Symbol == symbol, ct);

            if (attempt == null)
                return 0;

            var backoff = Math.Min((int)Math.Pow(2, attempt.AttemptCount - 1), 300);
            return backoff;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get exit backoff for symbol {symbol}", symbol);
            throw new StateRepositoryException($"Failed to get exit backoff", ex);
        }
    }

    /// <summary>
    /// Gets current circuit breaker count.
    /// </summary>
    public async ValueTask<int> GetCircuitBreakerCountAsync(CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var state = await dbContext.CircuitBreakerState
                .FirstOrDefaultAsync(x => x.Id == 1, ct);

            return state?.Count ?? 0;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get circuit breaker count");
            throw new StateRepositoryException($"Failed to get circuit breaker count", ex);
        }
    }

    /// <summary>
    /// Saves circuit breaker count.
    /// </summary>
    public async ValueTask SaveCircuitBreakerCountAsync(int count, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var state = await dbContext.CircuitBreakerState
                .FirstOrDefaultAsync(x => x.Id == 1, ct);

            if (state == null)
            {
                dbContext.CircuitBreakerState.Add(new CircuitBreakerStateEntity
                {
                    Id = 1,
                    Count = count,
                    LastResetAt = DateTimeOffset.UtcNow
                });
            }
            else
            {
                state.Count = count;
            }

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save circuit breaker count");
            throw new StateRepositoryException($"Failed to save circuit breaker count", ex);
        }
    }

    /// <summary>
    /// Atomically increments daily_trade_count by 1.
    /// Uses Serializable isolation to prevent lost updates from concurrent fills.
    /// </summary>
    public async ValueTask IncrementDailyTradeCountAsync(CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            try
            {
                var entity = await dbContext.BotState
                    .FirstOrDefaultAsync(x => x.Key == "daily_trade_count", ct);
                var count = int.TryParse(entity?.Value, out var v) ? v : 0;
                if (entity == null)
                    dbContext.BotState.Add(new BotStateEntity
                        { Key = "daily_trade_count", Value = (count + 1).ToString(), UpdatedAt = DateTimeOffset.UtcNow });
                else
                {
                    entity.Value = (count + 1).ToString();
                    entity.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await dbContext.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        catch (StateRepositoryException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to increment daily trade count");
            throw new StateRepositoryException("Failed to increment daily trade count", ex);
        }
    }

    /// <summary>
    /// Atomically adds pnlDelta to daily_realized_pnl (negative value = loss).
    /// Uses Serializable isolation to prevent lost updates from concurrent fills.
    /// </summary>
    public async ValueTask AddDailyRealizedPnlAsync(decimal pnlDelta, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);
            try
            {
                var entity = await dbContext.BotState
                    .FirstOrDefaultAsync(x => x.Key == "daily_realized_pnl", ct);
                // Use InvariantCulture for both parsing and formatting
                var pnl = decimal.TryParse(entity?.Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0m;
                var newPnl = pnl + pnlDelta;
                
                if (entity == null)
                    dbContext.BotState.Add(new BotStateEntity
                        { Key = "daily_realized_pnl", Value = newPnl.ToString(CultureInfo.InvariantCulture), UpdatedAt = DateTimeOffset.UtcNow });
                else
                {
                    entity.Value = newPnl.ToString(CultureInfo.InvariantCulture);
                    entity.UpdatedAt = DateTimeOffset.UtcNow;
                }
                await dbContext.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        catch (StateRepositoryException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to add to daily realized PnL");
            throw new StateRepositoryException("Failed to add to daily realized PnL", ex);
        }
    }

    /// <summary>
    /// Resets daily state (circuit breaker, trade count, PnL) in a single DbContext/transaction
    /// so a crash between steps cannot leave the daily state partially reset.
    /// </summary>
    public async ValueTask ResetDailyStateAsync(CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);

            // Reset circuit breaker
            var cbState = await dbContext.CircuitBreakerState
                .FirstOrDefaultAsync(x => x.Id == 1, ct);
            if (cbState != null)
            {
                cbState.Count = 0;
                cbState.LastResetAt = DateTimeOffset.UtcNow;
            }

            // Reset KV counters in the same DbContext so all three writes share one transaction
            await UpsertBotStateInContextAsync(dbContext, "daily_realized_pnl", "0", ct);
            await UpsertBotStateInContextAsync(dbContext, "daily_trade_count", "0", ct);

            await dbContext.SaveChangesAsync(ct);
        }
        catch (StateRepositoryException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset daily state");
            throw new StateRepositoryException($"Failed to reset daily state", ex);
        }
    }

    /// <summary>
    /// Upserts a bot_state KV row within an already-open DbContext (no SaveChanges — caller commits).
    /// </summary>
    private static async ValueTask UpsertBotStateInContextAsync(
        TradingDbContext dbContext, string key, string value, CancellationToken ct)
    {
        var entity = await dbContext.BotState.FirstOrDefaultAsync(x => x.Key == key, ct);
        if (entity == null)
            dbContext.BotState.Add(new BotStateEntity { Key = key, Value = value, UpdatedAt = DateTimeOffset.UtcNow });
        else
        {
            entity.Value = value;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
        }
    }

    /// <summary>
    /// Records an exit attempt for backoff tracking (idempotent).
    /// </summary>
    public async ValueTask RecordExitAttemptAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var attempt = await dbContext.ExitAttempts
                .FirstOrDefaultAsync(x => x.Symbol == symbol, ct);

            if (attempt == null)
            {
                dbContext.ExitAttempts.Add(new ExitAttemptEntity
                {
                    Symbol = symbol,
                    AttemptCount = 1,
                    LastAttemptAt = DateTimeOffset.UtcNow,
                    NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(1)
                });
            }
            else
            {
                attempt.AttemptCount++;
                attempt.LastAttemptAt = DateTimeOffset.UtcNow;
                attempt.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Min((int)Math.Pow(2, attempt.AttemptCount - 1), 300));
            }

            await dbContext.SaveChangesAsync(ct);
            logger.LogDebug("Recorded exit attempt for {symbol}", symbol);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record exit attempt for {symbol}", symbol);
            throw new StateRepositoryException($"Failed to record exit attempt for {symbol}", ex);
        }
    }

    /// <summary>
    /// Records a failed exit attempt, incrementing count for exponential backoff.
    /// </summary>
    public async ValueTask RecordExitAttemptFailureAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var attempt = await dbContext.ExitAttempts
                .FirstOrDefaultAsync(x => x.Symbol == symbol, ct);

            if (attempt != null)
            {
                attempt.LastAttemptAt = DateTimeOffset.UtcNow;
                attempt.NextRetryAt = DateTimeOffset.UtcNow.AddSeconds(
                    Math.Min((int)Math.Pow(2, Math.Max(attempt.AttemptCount, 1)), 300));

                await dbContext.SaveChangesAsync(ct);
                logger.LogWarning("Recorded exit attempt failure for {symbol}: next retry in {seconds}s",
                    symbol, (attempt.NextRetryAt - DateTimeOffset.UtcNow).TotalSeconds);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record exit attempt failure for {symbol}", symbol);
            throw new StateRepositoryException($"Failed to record exit attempt failure for {symbol}", ex);
        }
    }

    /// <summary>
    /// Inserts equity snapshot to equity_curve table (idempotent by timestamp).
    /// </summary>
    public async ValueTask InsertEquitySnapshotAsync(
        DateTimeOffset timestamp,
        decimal portfolioValue,
        decimal cashBalance,
        decimal dailyPnl,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var existing = await dbContext.EquityCurve
                .FirstOrDefaultAsync(x => x.Timestamp == timestamp, ct);

            if (existing != null)
            {
                logger.LogDebug("Equity snapshot already exists for {timestamp}", timestamp);
                return;
            }

            dbContext.EquityCurve.Add(new EquityCurveEntity
            {
                Timestamp = timestamp,
                PortfolioValue = portfolioValue,
                CashBalance = cashBalance,
                DailyPnl = dailyPnl,
                CumulativePnl = 0m
            });

            await dbContext.SaveChangesAsync(ct);
            logger.LogDebug("Equity snapshot inserted: portfolio={value} cash={cash} pnl={pnl}",
                portfolioValue, cashBalance, dailyPnl);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to insert equity snapshot");
            throw new StateRepositoryException($"Failed to insert equity snapshot", ex);
        }
    }

    /// <summary>
    /// Inserts reconciliation report (JSON) to reconciliation_reports table.
    /// </summary>
    public async ValueTask InsertReconciliationReportAsync(
        string reportJson,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            dbContext.ReconciliationReports.Add(new ReconciliationReportEntity
            {
                ReportDate = DateTimeOffset.UtcNow,
                OrdersProcessed = 0,
                TradesCompleted = 0,
                TotalPnl = 0m,
                Status = reportJson
            });
            await dbContext.SaveChangesAsync(ct);
            logger.LogDebug("Reconciliation report persisted");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to insert reconciliation report");
            throw new StateRepositoryException($"Failed to insert reconciliation report", ex);
        }
    }

    /// <summary>
    /// Gets all order intents from database.
    /// </summary>
    public async ValueTask<IReadOnlyList<OrderIntentDto>> GetAllOrderIntentsAsync(CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var intents = await dbContext.OrderIntents
                .ToListAsync(ct);

            var dtos = new List<OrderIntentDto>(intents.Count);
            foreach (var intent in intents)
            {
                dtos.Add(new OrderIntentDto(
                    ClientOrderId: intent.ClientOrderId,
                    AlpacaOrderId: intent.AlpacaOrderId,
                    Symbol: intent.Symbol,
                    Side: intent.Side,
                    Quantity: intent.Quantity,
                    LimitPrice: intent.LimitPrice,
                    Status: Enum.Parse<OrderState>(intent.Status),
                    CreatedAt: intent.CreatedAt,
                    UpdatedAt: intent.UpdatedAt,
                    AtrSeed: intent.AtrSeed));
            }

            return dtos.AsReadOnly();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get all order intents");
            throw new StateRepositoryException($"Failed to get all order intents", ex);
        }
    }

    /// <summary>
    /// Returns true if any non-terminal order intent exists for the given symbol+side.
    /// Non-terminal: PendingNew, Accepted, PartiallyFilled, PendingCancel, PendingReplace.
    /// </summary>
    public async ValueTask<bool> HasPendingOrderAsync(string symbol, string side, CancellationToken ct = default)
    {
        try
        {
            // Statuses considered non-terminal (still in-flight, could receive fills)
            var nonTerminalStatuses = new[]
            {
                OrderState.PendingNew.ToString(),
                OrderState.Accepted.ToString(),
                OrderState.PartiallyFilled.ToString(),
                OrderState.PendingCancel.ToString(),
                OrderState.PendingReplace.ToString(),
            };

            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            return await dbContext.OrderIntents.AnyAsync(
                x => x.Symbol == symbol &&
                     x.Side == side &&
                     nonTerminalStatuses.Contains(x.Status),
                ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to check pending orders for {symbol} {side}", symbol, side);
            throw new StateRepositoryException($"Failed to check pending orders for {symbol} {side}", ex);
        }
    }

    /// <summary>
    /// Gets all position tracking records (symbol, qty, entry price, ATR, trailing stop).
    /// </summary>
    public async ValueTask<IReadOnlyList<(string Symbol, decimal Quantity, decimal EntryPrice, decimal AtrValue, decimal TrailingStopPrice)>>
        GetAllPositionTrackingAsync(CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var positions = await dbContext.PositionTracking
                .ToListAsync(ct);

            var result = new List<(string Symbol, decimal Quantity, decimal EntryPrice, decimal AtrValue, decimal TrailingStopPrice)>(positions.Count);
            foreach (var pos in positions)
            {
                result.Add((pos.Symbol, pos.CurrentQuantity, pos.EntryPrice, pos.AtrValue, pos.TrailingStopPrice));
            }

            return result.AsReadOnly();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get all position tracking records");
            throw new StateRepositoryException($"Failed to get all position tracking records", ex);
        }
    }

    /// <summary>
    /// Upserts a position tracking row (find-or-create on Symbol).
    /// Set qty/entryPrice/atr/trailingStop to 0 to mark a position as closed.
    /// Retries up to 3 times on concurrent-insert conflicts (unique index on Symbol).
    /// </summary>
    public async ValueTask UpsertPositionTrackingAsync(
        string symbol,
        decimal qty,
        decimal entryPrice,
        decimal atrValue,
        decimal trailingStop,
        CancellationToken ct = default)
    {
        const int maxRetries = 3;
        for (var attempt = 0; attempt < maxRetries; attempt++)
        {
            try
            {
                await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
                var entity = await dbContext.PositionTracking
                    .FirstOrDefaultAsync(x => x.Symbol == symbol, ct);

                if (entity == null)
                {
                    dbContext.PositionTracking.Add(new PositionTrackingEntity
                    {
                        Symbol = symbol,
                        CurrentQuantity = qty,
                        EntryPrice = entryPrice,
                        AtrValue = atrValue,
                        TrailingStopPrice = trailingStop,
                        LastUpdateAt = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    entity.CurrentQuantity = qty;
                    entity.EntryPrice = entryPrice;
                    entity.AtrValue = atrValue;
                    entity.TrailingStopPrice = trailingStop;
                    entity.LastUpdateAt = DateTimeOffset.UtcNow;
                }

                await dbContext.SaveChangesAsync(ct);
                logger.LogDebug("Upserted position_tracking: {symbol} qty={qty} entry={entry}", symbol, qty, entryPrice);
                return;
            }
            catch (DbUpdateException dbEx) when (attempt < maxRetries - 1)
            {
                // Concurrent insert for the same symbol hit the unique index; retry with fresh context.
                logger.LogWarning(dbEx,
                    "Concurrency conflict upserting position_tracking for {symbol} (attempt {attempt}), retrying",
                    symbol, attempt + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to upsert position tracking for {symbol}", symbol);
                throw new StateRepositoryException($"Failed to upsert position tracking for {symbol}", ex);
            }
        }
    }

    /// <summary>
    /// Gets the persisted drawdown state. Returns null if no record exists yet.
    /// </summary>
    public async ValueTask<DrawdownStateDto?> GetDrawdownStateAsync(CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var entity = await dbContext.DrawdownState
                .FirstOrDefaultAsync(x => x.Id == 1, ct);

            if (entity == null)
                return null;

            return new DrawdownStateDto(
                Level: Enum.Parse<DrawdownLevel>(entity.Level),
                PeakEquity: entity.PeakEquity,
                CurrentDrawdownPct: entity.CurrentDrawdownPct,
                LastUpdated: entity.LastUpdated,
                LastPeakResetTime: entity.LastPeakResetTime,
                ManualRecoveryRequested: entity.ManualRecoveryRequested);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get drawdown state");
            throw new StateRepositoryException("Failed to get drawdown state", ex);
        }
    }

    /// <summary>
    /// Persists the current drawdown state (upsert on single row Id=1).
    /// </summary>
    public async ValueTask SaveDrawdownStateAsync(
        DrawdownLevel level,
        decimal peakEquity,
        decimal drawdownPct,
        DateTimeOffset lastPeakResetTime,
        bool manualRecoveryRequested,
        CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            var entity = await dbContext.DrawdownState
                .FirstOrDefaultAsync(x => x.Id == 1, ct);

            if (entity == null)
            {
                dbContext.DrawdownState.Add(new DrawdownStateEntity
                {
                    Id = 1,
                    Level = level.ToString(),
                    PeakEquity = peakEquity,
                    CurrentDrawdownPct = drawdownPct,
                    LastUpdated = DateTimeOffset.UtcNow,
                    LastPeakResetTime = lastPeakResetTime,
                    ManualRecoveryRequested = manualRecoveryRequested
                });
            }
            else
            {
                entity.Level = level.ToString();
                entity.PeakEquity = peakEquity;
                entity.CurrentDrawdownPct = drawdownPct;
                entity.LastUpdated = DateTimeOffset.UtcNow;
                entity.LastPeakResetTime = lastPeakResetTime;
                entity.ManualRecoveryRequested = manualRecoveryRequested;
            }

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to save drawdown state");
            throw new StateRepositoryException("Failed to save drawdown state", ex);
        }
    }

    /// <summary>
    /// Atomically checks if daily reset is needed and marks it as done.
    /// Returns true if reset should proceed, false if already reset today.
    /// Uses Serializable isolation to prevent TOCTOU race conditions.
    /// </summary>
    public async ValueTask<bool> TryAcquireDailyResetAsync(string todayDateStr, CancellationToken ct = default)
    {
        try
        {
            await using var dbContext = await DbFactory.CreateDbContextAsync(ct);
            await using var tx = await dbContext.Database.BeginTransactionAsync(
                System.Data.IsolationLevel.Serializable, ct);

            try
            {
                // Check current daily_reset_date
                var currentState = await dbContext.BotState
                    .FirstOrDefaultAsync(x => x.Key == "daily_reset_date", ct);

                // If already reset today, reject
                if (currentState?.Value == todayDateStr)
                {
                    await tx.RollbackAsync(ct);
                    logger.LogDebug("Daily reset already performed for {date}, rejected", todayDateStr);
                    return false;
                }

                // Accept: update the date atomically
                if (currentState == null)
                {
                    dbContext.BotState.Add(new BotStateEntity
                    {
                        Key = "daily_reset_date",
                        Value = todayDateStr,
                        UpdatedAt = DateTimeOffset.UtcNow
                    });
                }
                else
                {
                    currentState.Value = todayDateStr;
                    currentState.UpdatedAt = DateTimeOffset.UtcNow;
                }

                await dbContext.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);

                logger.LogDebug("Daily reset acquired for {date}", todayDateStr);
                return true;
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to acquire daily reset for {date}", todayDateStr);
            throw new StateRepositoryException($"Failed to acquire daily reset for {todayDateStr}", ex);
        }
    }
}
