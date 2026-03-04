namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Runtime reconciliation service (Phase 4).
/// Periodic check (default 120s) for discrepancies and repair stuck exits.
/// Sets bot_state flags: trading_halted, broker_health.
/// </summary>
public sealed class RuntimeReconcilerService(
    IBrokerService brokerService,
    IStateRepository stateRepository,
    PositionTracker positionTracker,
    ILogger<RuntimeReconcilerService> logger,
    IOptions<RuntimeReconciliationOptions> options,
    IMarketDataClient? marketDataClient = null) : BackgroundService
{
    private readonly RuntimeReconciliationOptions _options = options.Value;
    private int _consecutiveFailures;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RuntimeReconciler starting with {interval}s check interval",
            _options.CheckIntervalSeconds);

        // Validate check interval range
        var checkInterval = Math.Max(1, Math.Min(300, _options.CheckIntervalSeconds));

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(checkInterval), stoppingToken);

                try
                {
                    await RunReconciliationCheckAsync(stoppingToken);
                    _consecutiveFailures = 0;
                    await stateRepository.SetStateAsync("broker_health", "healthy", stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    logger.LogError(ex, "Reconciliation check failed (attempt {count}/3)",
                        _consecutiveFailures);

                    if (_consecutiveFailures >= 3)
                    {
                        logger.LogWarning("Degrading to warning-only mode after 3 failures");
                        await stateRepository.SetStateAsync("broker_health", "degraded", stoppingToken);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("RuntimeReconciler stopped");
        }
    }

    /// <summary>
    /// Runs the reconciliation check: repairs stuck exits, persists report.
    /// </summary>
    private async ValueTask RunReconciliationCheckAsync(CancellationToken ct)
    {
        var startTime = DateTimeOffset.UtcNow;
        var discrepancies = new List<string>();

        try
        {
            // Repair stuck exits
            await RepairStuckExitsAsync(ct);

            // Reconcile positions: repair both directions so PositionTracker stays in sync with Alpaca.
            var alpacaPositions = await brokerService.GetPositionsAsync(ct);
            var trackedKeys = positionTracker.GetAllPositions().Keys.ToList(); // snapshot before mutation

            // Ghost positions: tracked but no longer in Alpaca → remove
            foreach (var symbol in trackedKeys)
            {
                if (alpacaPositions.All(ap => ap.Symbol != symbol))
                {
                    positionTracker.ClosePosition(symbol);
                    discrepancies.Add($"Removed ghost position: {symbol}");
                    logger.LogWarning("Reconciliation: removed ghost position {symbol} from tracker", symbol);
                }
            }

            // Missing positions: in Alpaca but not in tracker → add.
            // ATR is estimated from recent daily bars so exit levels are active immediately.
            // Falls back to 0 when unavailable; ExitManager safely skips zero-ATR positions.
            foreach (var alpacaPos in alpacaPositions)
            {
                if (!trackedKeys.Contains(alpacaPos.Symbol))
                {
                    var atr = await EstimateAtrAsync(alpacaPos.Symbol, ct);
                    positionTracker.OpenPosition(
                        alpacaPos.Symbol, alpacaPos.Quantity, alpacaPos.AverageEntryPrice, atrValue: atr);
                    discrepancies.Add($"Added missing position: {alpacaPos.Symbol}");
                    logger.LogWarning(
                        "Reconciliation: added missing position {symbol} qty={qty} entry={entry} atr={atr:F4}",
                        alpacaPos.Symbol, alpacaPos.Quantity, alpacaPos.AverageEntryPrice, atr);
                }
            }

            // After repair, positions are consistent — keep trading running
            await stateRepository.SetStateAsync("trading_halted", "false", ct);
            if (discrepancies.Any())
                logger.LogInformation("Reconciliation repaired {count} discrepancy/ies", discrepancies.Count);

            // Persist report
            await PersistReconciliationReportAsync(startTime, discrepancies, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error in reconciliation check");
            throw;
        }
    }

    /// <summary>
    /// Repairs stuck exits: clears pending_exit if position no longer exists.
    /// </summary>
    private async ValueTask RepairStuckExitsAsync(CancellationToken ct)
    {
        try
        {
            var alpacaPositions = await brokerService.GetPositionsAsync(ct);
            var alpacaOrders = await brokerService.GetOpenOrdersAsync(ct);
            var positions = positionTracker.GetAllPositions();

            foreach (var (symbol, posData) in positions)
            {
                if (!posData.PendingExit)
                {
                    continue;
                }

                // Check if position still exists in Alpaca
                if (alpacaPositions.Any(p => p.Symbol == symbol))
                {
                    continue;
                }

                // Check if any working exit order exists
                if (alpacaOrders.Any(o => o.Symbol == symbol && !IsTerminal(o.Status)))
                {
                    continue;
                }

                // Position gone and no working exit order: clear pending_exit
                posData.PendingExit = false;
                logger.LogWarning(
                    "Repaired stuck exit for {symbol}: position/order no longer exists",
                    symbol);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error repairing stuck exits");
        }
    }

    /// <summary>
    /// Persists reconciliation report to reconciliation_reports table.
    /// </summary>
    private async ValueTask PersistReconciliationReportAsync(
        DateTimeOffset startTime,
        List<string> discrepancies,
        CancellationToken ct)
    {
        try
        {
            var duration = DateTimeOffset.UtcNow - startTime;
            var reportJson = System.Text.Json.JsonSerializer.Serialize(new
            {
                CheckedAt = startTime,
                DurationMs = duration.TotalMilliseconds,
                DiscrepancyCount = discrepancies.Count,
                Discrepancies = discrepancies,
                Status = discrepancies.Any() ? "FAILED" : "PASSED"
            });

            await stateRepository.InsertReconciliationReportAsync(reportJson, ct);

            logger.LogDebug("Reconciliation report persisted (duration {duration}ms discrepancies {count})",
                duration.TotalMilliseconds, discrepancies.Count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist reconciliation report");
        }
    }

    /// <summary>
    /// Estimates ATR from 30 recent daily bars (14-period). Returns 0 when unavailable.
    /// Used to seed exit levels for positions added during reconciliation so the ExitManager
    /// can protect them immediately rather than waiting for the next organic fill.
    /// </summary>
    private async ValueTask<decimal> EstimateAtrAsync(string symbol, CancellationToken ct)
    {
        if (marketDataClient == null)
            return 0m;

        try
        {
            const int barCount = 30;
            const int atrPeriod = 14;

            var bars = await marketDataClient.GetBarsAsync(symbol, "1Day", barCount, ct);
            if (bars.Count < atrPeriod)
            {
                logger.LogDebug(
                    "Insufficient bars to estimate ATR for {symbol}: {count}/{required}",
                    symbol, bars.Count, atrPeriod);
                return 0m;
            }

            var history = new BarHistory(barCount);
            foreach (var bar in bars)
                history.AddBar(bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);

            return history.CalculateAtr(atrPeriod);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to estimate ATR for {symbol}, falling back to 0", symbol);
            return 0m;
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
            OrderState.PartiallyFilled => true,
            _ => false
        };
    }
}

/// <summary>
/// Configuration options for runtime reconciliation.
/// </summary>
public sealed class RuntimeReconciliationOptions
{
    /// <summary>
    /// Check interval in seconds (default 120s, range 30-300s).
    /// </summary>
    public int CheckIntervalSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum consecutive failures before degrading to warning-only.
    /// </summary>
    public int MaxConsecutiveFailures { get; set; } = 3;
}
