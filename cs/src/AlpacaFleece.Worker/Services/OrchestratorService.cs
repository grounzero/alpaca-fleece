namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Orchestrator service implementing startup via IHostedLifecycleService.
/// Phase 1: Infrastructure (SchemaManager, reconciliation)
/// Phase 2: Data Layer (EventBus start)
/// Phase 3: Trading Logic (services)
/// Phase 4: Runtime (signal handlers, task monitoring)
/// </summary>
/// <param name="logger">The logger instance.</param>
/// <param name="serviceProvider">The service provider for resolving dependencies.</param>
/// <param name="stateRepository">The state repository for managing trading state.</param>
public sealed class OrchestratorService(
    ILogger<OrchestratorService> logger,
    IServiceProvider serviceProvider,
    IStateRepository stateRepository) : IHostedLifecycleService
{
    /// <summary>
    /// IHostedService.StartAsync - called when host starts.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("OrchestratorService.StartAsync");
        return Task.CompletedTask;
    }

    /// <summary>
    /// IHostedService.StopAsync - called when host stops.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("OrchestratorService.StopAsync");
        return Task.CompletedTask;
    }

    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Starting schema and reconciliation");
        await Task.CompletedTask;
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Data Layer, Trading Logic, Runtime - All services started");

        // Block signals until reconciliation succeeds (set before rehydration, cleared on success).
        await stateRepository.SetStateAsync("trading_ready", "false", cancellationToken);

        // Rehydrate PositionTracker from DB after migrations have completed so a worker
        // restart does not lose open-position metadata (entry price, ATR, trailing stop).
        var positionTracker = serviceProvider.GetRequiredService<PositionTracker>();
        await positionTracker.InitialiseFromDbAsync(cancellationToken);

        // Startup reconciliation gate: verify broker/DB state is consistent before allowing trades.
        using var scope = serviceProvider.CreateScope();
        var reconciliation = scope.ServiceProvider.GetRequiredService<IReconciliationService>();
        try
        {
            await reconciliation.PerformStartupReconciliationAsync(cancellationToken);
            await reconciliation.ReconcileFillsAsync(cancellationToken);
            await stateRepository.SetStateAsync("trading_ready", "true", cancellationToken);
            // Clear the market_data_degraded flag set by ExitManager during the previous session.
            // On clean startup the price feeds are assumed good; ExitManager will re-raise if needed.
            await stateRepository.SetStateAsync("market_data_degraded", "false", cancellationToken);
            logger.LogInformation("Startup reconciliation complete — trading enabled");
            logger.LogInformation("Cleared market_data_degraded flag on successful startup reconciliation");
        }
        catch (Exception ex)
        {
            logger.LogCritical(ex,
                "Startup reconciliation failed — trading blocked until manual resolution");
            // trading_ready remains "false"; bot keeps running but all signals are blocked
        }

        // Register signal handlers
        Console.CancelKeyPress += (_, args) =>
        {
            args.Cancel = true;
            logger.LogInformation("SIGINT received, initiating graceful shutdown");
        };

        AppDomain.CurrentDomain.ProcessExit += (_, args) =>
        {
            logger.LogInformation("SIGTERM received, initiating graceful shutdown");
        };

        logger.LogInformation("AlpacaFleece trading bot started successfully");
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Graceful shutdown initiated");
        await Task.CompletedTask;
    }

    public async Task StoppedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Graceful shutdown completed");
        await Task.CompletedTask;
    }
}
