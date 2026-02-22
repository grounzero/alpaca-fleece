namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Schema manager service: runs migrations on startup.
/// </summary>
public sealed class SchemaManagerService(
    IServiceProvider serviceProvider,
    ILogger<SchemaManagerService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Applying database migrations");

            using var scope = serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<TradingDbContext>();

            await dbContext.Database.MigrateAsync(cancellationToken);

            logger.LogInformation("Database migrations completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply database migrations");
            throw;
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
