namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Stub services for Phase 2-6 implementation (placeholders).
/// </summary>

public sealed class StubStreamPollerService(ILogger<StubStreamPollerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("StreamPollerService stub started (Phase 2)");
        await Task.Delay(-1, stoppingToken);
    }
}

public sealed class StubStrategyService(ILogger<StubStrategyService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("StrategyService stub started (Phase 3)");
        await Task.Delay(-1, stoppingToken);
    }
}

public sealed class StubRiskManagerService(ILogger<StubRiskManagerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("RiskManagerService stub started (Phase 3)");
        await Task.Delay(-1, stoppingToken);
    }
}

public sealed class StubOrderManagerService(ILogger<StubOrderManagerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("OrderManagerService stub started (Phase 3)");
        await Task.Delay(-1, stoppingToken);
    }
}

public sealed class StubExitManagerService(ILogger<StubExitManagerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExitManagerService stub started (Phase 4)");
        await Task.Delay(-1, stoppingToken);
    }
}

public sealed class StubReconciliationService(ILogger<StubReconciliationService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ReconciliationService stub started (Phase 5)");
        await Task.Delay(-1, stoppingToken);
    }
}

public sealed class StubHousekeepingService(ILogger<StubHousekeepingService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HousekeepingService stub started (Phase 6)");
        await Task.Delay(-1, stoppingToken);
    }
}
