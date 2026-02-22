namespace AlpacaFleece.Infrastructure.Repositories;

/// <summary>
/// State repository implementation with atomic gate logic and KV store.
/// </summary>
public sealed class StateRepository(
    TradingDbContext dbContext,
    ILogger<StateRepository> logger) : IStateRepository
{
    /// <summary>
    /// Gets a KV pair from bot_state.
    /// </summary>
    public async ValueTask<string?> GetStateAsync(string key, CancellationToken ct = default)
    {
        try
        {
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
            using var tx = await dbContext.Database.BeginTransactionAsync(
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
        int quantity,
        decimal limitPrice,
        DateTimeOffset createdAt,
        CancellationToken ct = default)
    {
        try
        {
            // Idempotency: return silently if already exists
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
                Status = OrderState.PendingNew.ToString(),
                CreatedAt = createdAt
            });

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            dbContext.ChangeTracker.Clear(); // prevent cascade failures from poisoned entity state
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
            var intent = await dbContext.OrderIntents
                .FirstOrDefaultAsync(x => x.ClientOrderId == clientOrderId, ct);

            if (intent == null)
                throw new StateRepositoryException($"Order intent {clientOrderId} not found");

            intent.AlpacaOrderId = alpacaOrderId;
            intent.Status = status.ToString();
            intent.UpdatedAt = updatedAt;

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
                UpdatedAt: intent.UpdatedAt);
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
        int filledQuantity,
        decimal filledPrice,
        string dedupeKey,
        DateTimeOffset filledAt,
        CancellationToken ct = default)
    {
        try
        {
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
    /// Resets daily state (circuit breaker, trade count, etc.).
    /// </summary>
    public async ValueTask ResetDailyStateAsync(CancellationToken ct = default)
    {
        try
        {
            var state = await dbContext.CircuitBreakerState
                .FirstOrDefaultAsync(x => x.Id == 1, ct);

            if (state != null)
            {
                state.Count = 0;
                state.LastResetAt = DateTimeOffset.UtcNow;
            }

            await dbContext.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to reset daily state");
            throw new StateRepositoryException($"Failed to reset daily state", ex);
        }
    }

    /// <summary>
    /// Records an exit attempt for backoff tracking (idempotent).
    /// </summary>
    public async ValueTask RecordExitAttemptAsync(string symbol, CancellationToken ct = default)
    {
        try
        {
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
                    UpdatedAt: intent.UpdatedAt));
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
    /// Gets all position tracking records (symbol, qty, entry price, ATR).
    /// </summary>
    public async ValueTask<IReadOnlyList<(string Symbol, int Quantity, decimal EntryPrice, decimal AtrValue)>>
        GetAllPositionTrackingAsync(CancellationToken ct = default)
    {
        try
        {
            var positions = await dbContext.PositionTracking
                .ToListAsync(ct);

            var result = new List<(string Symbol, int Quantity, decimal EntryPrice, decimal AtrValue)>(positions.Count);
            foreach (var pos in positions)
            {
                result.Add((pos.Symbol, pos.CurrentQuantity, pos.EntryPrice, pos.AtrValue));
            }

            return result.AsReadOnly();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get all position tracking records");
            throw new StateRepositoryException($"Failed to get all position tracking records", ex);
        }
    }
}
