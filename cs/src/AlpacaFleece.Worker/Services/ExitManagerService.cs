namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Exit manager service (Phase 4): runs ExitManager.ExecuteAsync in background.
/// Wraps the synchronous ExitManager logic for hosted service integration.
/// </summary>
public sealed class ExitManagerService(
    ExitManager exitManager,
    ILogger<ExitManagerService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("ExitManagerService starting");

        try
        {
            await exitManager.ExecuteAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("ExitManagerService stopped");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ExitManagerService encountered error");
            throw;
        }
    }
}
