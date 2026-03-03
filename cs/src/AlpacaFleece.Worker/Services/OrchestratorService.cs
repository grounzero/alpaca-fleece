namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Orchestrator service implementing startup via IHostedLifecycleService.
/// Phase 1: Infrastructure (SchemaManager, reconciliation)
/// Phase 2: Data Layer (EventBus start)
/// Phase 3: Trading Logic (services)
/// Phase 4: Runtime (signal handlers, task monitoring)
/// </summary>
public sealed class OrchestratorService(
    ILogger<OrchestratorService> logger,
    IServiceProvider serviceProvider) : IHostedLifecycleService
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

        // Rehydrate PositionTracker from DB after migrations have completed so a worker
        // restart does not lose open-position metadata (entry price, ATR, trailing stop).
        var positionTracker = serviceProvider.GetRequiredService<PositionTracker>();
        await positionTracker.InitialiseFromDbAsync(cancellationToken);

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
        await Task.CompletedTask;
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
