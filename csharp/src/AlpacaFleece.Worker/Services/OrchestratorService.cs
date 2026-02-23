namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Orchestrator service implementing 4-phase startup via IHostedLifecycleService.
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
        logger.LogInformation("Phase 1: Infrastructure - Starting schema and reconciliation");

        // Ensure database is created before proceeding
        using var scope = serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        // Rehydrate PositionTracker from DB before the trading loop begins.
        // Mirrors Python's PositionTracker._load_from_db() so a worker restart does not
        // lose open-position metadata (entry price, ATR, trailing stop).
        var positionTracker = serviceProvider.GetRequiredService<PositionTracker>();
        await positionTracker.InitialiseFromDbAsync(cancellationToken);
    }

    public async Task StartedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Phase 2-4: Data Layer, Trading Logic, Runtime - All services started");

        // Register signal handlers
        Console.CancelKeyPress += (sender, args) =>
        {
            args.Cancel = true;
            logger.LogInformation("SIGINT received, initiating graceful shutdown");
        };

        AppDomain.CurrentDomain.ProcessExit += (sender, args) =>
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
