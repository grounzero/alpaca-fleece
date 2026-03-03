namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Stub services (placeholders).
/// </summary>

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

