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
    IOptions<RuntimeReconciliationOptions> options) : BackgroundService
{
    private readonly RuntimeReconciliationOptions _options = options.Value;
    private int _consecutiveFailures = 0;

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

            // Check for discrepancies (simplified)
            var alpacaPositions = await brokerService.GetPositionsAsync(ct);
            var trackedPositions = positionTracker.GetAllPositions();

            foreach (var tracked in trackedPositions)
            {
                if (!alpacaPositions.Any(ap => ap.Symbol == tracked.Key))
                {
                    discrepancies.Add($"Position {tracked.Key} tracked but not in Alpaca");
                }
            }

            // Update bot_state
            if (discrepancies.Any())
            {
                logger.LogWarning("Found {count} discrepancies", discrepancies.Count);
                await stateRepository.SetStateAsync("trading_halted", "true", ct);
            }
            else
            {
                await stateRepository.SetStateAsync("trading_halted", "false", ct);
            }

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
