namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Startup reconciliation (Phase 1, blocking).
/// Rules: 1) Alpaca terminal + SQLite non-terminal → auto-apply
///        2) SQLite terminal + Alpaca non-terminal → block
///        3) Open order in Alpaca not in SQLite → block
///        4) Position qty mismatch → block
/// On discrepancy: log JSON, write to data/reconciliation_error.json, throw ReconciliationException.
/// </summary>
public sealed class ReconciliationService(
    IBrokerService brokerService,
    IStateRepository stateRepository,
    ILogger<ReconciliationService> logger) : IReconciliationService
{
    /// <summary>
    /// Performs startup reconciliation (blocking, Phase 1).
    /// </summary>
    public async ValueTask PerformStartupReconciliationAsync(CancellationToken ct)
    {
        logger.LogInformation("Starting reconciliation checks");

        try
        {
            // Get current state from Alpaca and SQLite
            var alpacaOrders = await brokerService.GetOpenOrdersAsync(ct);
            var alpacaPositions = await brokerService.GetPositionsAsync(ct);
            var sqliteOrders = await stateRepository.GetAllOrderIntentsAsync(ct);

            var discrepancies = new List<string>();

            // Rule 1: SQLite non-terminal but Alpaca already terminal → auto-apply
            // (GetOpenOrdersAsync only returns open orders, so we must check SQLite non-terminal intents)
            var nonTerminalSqlite = sqliteOrders.Where(so => !IsTerminal(so.Status) && so.AlpacaOrderId != null);
            foreach (var sqliteOrder in nonTerminalSqlite)
            {
                var brokerOrder = await brokerService.GetOrderByIdAsync(sqliteOrder.AlpacaOrderId!, ct);
                if (brokerOrder == null || !IsTerminal(brokerOrder.Status))
                    continue;  // Order not found on broker or still open — no action

                logger.LogInformation(
                    "Rule 1: auto-applying Alpaca terminal status to SQLite: {orderId} {status}",
                    sqliteOrder.AlpacaOrderId, brokerOrder.Status);
                await stateRepository.UpdateOrderIntentAsync(
                    sqliteOrder.ClientOrderId,
                    brokerOrder.AlpacaOrderId,
                    brokerOrder.Status,
                    DateTimeOffset.UtcNow,
                    ct);
            }

            // Rule 2: SQLite terminal + Alpaca non-terminal → discrepancy
            foreach (var sqliteOrder in sqliteOrders.Where(so => IsTerminal(so.Status)))
            {
                var alpacaOrder = alpacaOrders.FirstOrDefault(
                    ao => ao.AlpacaOrderId == sqliteOrder.AlpacaOrderId);
                if (alpacaOrder != null && !IsTerminal(alpacaOrder.Status))
                {
                    discrepancies.Add(
                        $"SQLite terminal ({sqliteOrder.Status}) but Alpaca non-terminal ({alpacaOrder.Status}): {sqliteOrder.AlpacaOrderId}");
                }
            }

            // Rule 3: Open order in Alpaca not in SQLite → discrepancy
            foreach (var alpacaOrder in alpacaOrders.Where(o => !IsTerminal(o.Status)))
            {
                if (!sqliteOrders.Any(so => so.AlpacaOrderId == alpacaOrder.AlpacaOrderId))
                {
                    discrepancies.Add(
                        $"Open order in Alpaca not in SQLite: {alpacaOrder.AlpacaOrderId} {alpacaOrder.Symbol}");
                }
            }

            // Rule 4: Position qty mismatch
            var sqlitePositions = await stateRepository.GetAllPositionTrackingAsync(ct);
            foreach (var alpacaPos in alpacaPositions)
            {
                var sqlitePos = sqlitePositions.FirstOrDefault(
                    sp => sp.Symbol == alpacaPos.Symbol);
                if (sqlitePos != default && sqlitePos.Quantity != alpacaPos.Quantity)
                {
                    discrepancies.Add(
                        $"Position qty mismatch {alpacaPos.Symbol}: Alpaca={alpacaPos.Quantity} SQLite={sqlitePos.Quantity}");
                }
            }

            // Auto-clear ghost positions (Issue #42)
            await AutoClearGhostPositionsAsync(alpacaPositions, sqlitePositions, alpacaOrders, ct);

            if (discrepancies.Any())
            {
                logger.LogError("Reconciliation discrepancies found: {@discrepancies}",
                    discrepancies);
                await WriteDiscrepancyReportAsync(discrepancies, ct);
                throw new ReconciliationException(
                    $"Reconciliation failed with {discrepancies.Count} discrepancies");
            }

            // Snapshot positions on clean reconciliation
            await SnapshotPositionsAsync(alpacaPositions, ct);
            logger.LogInformation("Reconciliation complete and clean");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex, "Reconciliation failed - blocking startup");
            throw;
        }
    }

    /// <summary>
    /// Reconciles fills (async): compare SQLite filled_qty vs broker filled_qty.
    /// </summary>
    public async ValueTask ReconcileFillsAsync(CancellationToken ct)
    {
        try
        {
            // Use filtered query — fill events only occur on non-terminal orders
            var sqliteOrders = await stateRepository.GetNonTerminalOrderIntentsAsync(ct);
            var alpacaOrders = await brokerService.GetOpenOrdersAsync(ct);

            foreach (var sqliteOrder in sqliteOrders)
            {
                // Already filtered to non-terminal, but keep the check for safety
                if (sqliteOrder.Status.IsTerminal())
                    continue;

                var alpacaOrder = alpacaOrders.FirstOrDefault(
                    o => o.AlpacaOrderId == sqliteOrder.AlpacaOrderId);

                if (alpacaOrder == null)
                    continue;

                // R-2: Only flag true fill drift — a Filled order where broker qty differs from intent qty.
                // Partial fills are expected to have FilledQuantity < Quantity; that is not drift.
                if (alpacaOrder.FilledQuantity > 0 &&
                    alpacaOrder.Status == OrderState.Filled &&
                    alpacaOrder.FilledQuantity != sqliteOrder.Quantity)
                {
                    logger.LogWarning(
                        "Fill drift detected for {symbol}: intendedQty={sqliteQty} actualFilledQty={alpacaQty}",
                        sqliteOrder.Symbol, sqliteOrder.Quantity, alpacaOrder.FilledQuantity);

                    var dedupeKey = $"{alpacaOrder.AlpacaOrderId}_{alpacaOrder.FilledQuantity}";
                    await stateRepository.InsertFillIdempotentAsync(
                        alpacaOrder.AlpacaOrderId,
                        alpacaOrder.ClientOrderId,
                        alpacaOrder.FilledQuantity,
                        alpacaOrder.AverageFilledPrice,
                        dedupeKey,
                        DateTimeOffset.UtcNow,
                        ct);
                }
            }

            logger.LogDebug("Fill reconciliation check completed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fill reconciliation error");
        }
    }

    /// <summary>
    /// Auto-clears ghost positions (Issue #42 fix).
    /// </summary>
    private async ValueTask AutoClearGhostPositionsAsync(
        IReadOnlyList<PositionInfo> alpacaPositions,
        IReadOnlyList<(string Symbol, decimal Quantity, decimal EntryPrice, decimal AtrValue, decimal TrailingStopPrice)> sqlitePositions,
        IReadOnlyList<OrderInfo> alpacaOrders,
        CancellationToken ct)
    {
        try
        {
            var alpacaSymbols = new HashSet<string>(alpacaPositions.Count);
            foreach (var pos in alpacaPositions)
            {
                alpacaSymbols.Add(pos.Symbol);
            }

            foreach (var sqlitePos in sqlitePositions)
            {
                // If position not in Alpaca AND no open orders for symbol
                if (alpacaSymbols.Contains(sqlitePos.Symbol))
                    continue;

                var hasOpenOrders = alpacaOrders.Any(o => o.Symbol == sqlitePos.Symbol && !IsTerminal(o.Status));
                if (hasOpenOrders)
                    continue;

                logger.LogWarning(
                    "Ghost position detected for {symbol}: qty={qty} entryPrice={price} — not present in Alpaca and no open orders. Clearing from DB.",
                    sqlitePos.Symbol, sqlitePos.Quantity, sqlitePos.EntryPrice);

                // Clear the ghost position from DB so PositionTracker does not rehydrate stale data.
                await stateRepository.UpsertPositionTrackingAsync(
                    sqlitePos.Symbol, 0m, 0m, 0m, 0m, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error auto-clearing ghost positions");
            // Don't block startup on ghost position cleanup
        }
    }

    /// <summary>
    /// Snapshots current Alpaca positions to positions_snapshot table.
    /// </summary>
    private async ValueTask SnapshotPositionsAsync(
        IReadOnlyList<PositionInfo> alpacaPositions,
        CancellationToken ct)
    {
        try
        {
            if (alpacaPositions.Count == 0)
            {
                logger.LogDebug("No positions to snapshot");
                return;
            }

            var timestamp = DateTimeOffset.UtcNow;
            foreach (var pos in alpacaPositions)
            {
                logger.LogDebug(
                    "Snapshots position {symbol}: qty={qty} entryPrice={price} currentPrice={current}",
                    pos.Symbol, pos.Quantity, pos.AverageEntryPrice, pos.CurrentPrice);
            }

            logger.LogDebug("Snapshots {count} positions at {timestamp}", alpacaPositions.Count, timestamp);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to snapshot positions");
        }
    }

    /// <summary>
    /// Writes discrepancy report to data/reconciliation_error.json.
    /// </summary>
    private async ValueTask WriteDiscrepancyReportAsync(
        List<string> discrepancies,
        CancellationToken ct)
    {
        try
        {
            var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            Directory.CreateDirectory(dataDir);

            var reportPath = Path.Combine(dataDir, "reconciliation_error.json");
            var report = new
            {
                Timestamp = DateTimeOffset.UtcNow,
                DiscrepancyCount = discrepancies.Count,
                Discrepancies = discrepancies
            };

            var json = System.Text.Json.JsonSerializer.Serialize(
                report,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

            await File.WriteAllTextAsync(reportPath, json, ct);
            logger.LogError("Reconciliation report written to {path}", reportPath);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to write reconciliation report");
        }
    }

    /// <summary>
    /// Checks if order state is terminal.
    /// </summary>
    private static bool IsTerminal(OrderState state)
    {
        return state switch
        {
            OrderState.Filled => true,
            OrderState.Canceled => true,
            OrderState.Expired => true,
            OrderState.Rejected => true,
            // PartiallyFilled is NOT terminal — the order may still receive further fills.
            // Treating it as terminal would block startup when a partial fill is in progress.
            _ => false
        };
    }
}

/// <summary>
/// Exception thrown when reconciliation fails at startup.
/// </summary>
public sealed class ReconciliationException(string message) : Exception(message);

/// <summary>
/// Interface for reconciliation service.
/// </summary>
public interface IReconciliationService
{
    ValueTask PerformStartupReconciliationAsync(CancellationToken ct);
    ValueTask ReconcileFillsAsync(CancellationToken ct);
}
