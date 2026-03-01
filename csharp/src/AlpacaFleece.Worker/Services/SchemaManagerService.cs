namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Schema manager service: runs migrations on startup.
/// </summary>
public sealed class SchemaManagerService(
    IServiceProvider serviceProvider,
    IHostEnvironment hostEnvironment,
    ILogger<SchemaManagerService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Applying database migrations");

            using var scope = serviceProvider.CreateScope();
            var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TradingDbContext>>();
            await using var dbContext = await dbFactory.CreateDbContextAsync(cancellationToken);

            try
            {
                await dbContext.Database.MigrateAsync(cancellationToken);
            }
            catch (Microsoft.Data.Sqlite.SqliteException sqlEx) when (
                hostEnvironment.IsDevelopment() &&
                sqlEx.SqliteErrorCode == 1 && // SQLITE_ERROR
                sqlEx.Message?.Contains("already exists", StringComparison.OrdinalIgnoreCase) == true)
            {
                // Development-only workaround: Some dev environments may have an existing schema
                // created outside of EF migrations (e.g., manual EnsureCreated or prior schema manager).
                // This catch specifically targets "table already exists" errors (SQLITE_ERROR code 1)
                // and only applies in Development to avoid masking real migration failures in production.
                logger.LogWarning(
                    sqlEx,
                    "[Development only] Migration encountered 'table already exists' error (SqliteErrorCode={Code}); continuing",
                    sqlEx.SqliteErrorCode);
            }

            // Ensure DrawdownState table exists (for existing databases that were created before this table was added)
            await EnsureDrawdownStateTableAsync(dbContext, cancellationToken);

            logger.LogInformation("Database migrations completed successfully");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to apply database migrations");
            throw;
        }
    }

    private async Task EnsureDrawdownStateTableAsync(TradingDbContext dbContext, CancellationToken cancellationToken)
    {
        try
        {
            // Check if DrawdownState table exists by attempting to query it
            await dbContext.DrawdownState.CountAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning("DrawdownState table doesn't exist, creating it: {message}", ex.Message);

            // Create the DrawdownState table using raw SQL
#pragma warning disable EF1002
            await dbContext.Database.ExecuteSqlRawAsync(
                @"CREATE TABLE IF NOT EXISTS drawdown_state (
                    id INTEGER PRIMARY KEY,
                    level TEXT NOT NULL DEFAULT 'Normal',
                    peak_equity NUMERIC(10,4) NOT NULL,
                    current_drawdown_pct NUMERIC(6,4) NOT NULL,
                    last_updated TEXT NOT NULL,
                    last_peak_reset_time TEXT NOT NULL,
                    manual_recovery_requested INTEGER NOT NULL DEFAULT 0
                )",
                cancellationToken);
#pragma warning restore EF1002

            // Seed initial state if table was just created
            if (!await dbContext.DrawdownState.AnyAsync(cancellationToken))
            {
                dbContext.DrawdownState.Add(new DrawdownStateEntity
                {
                    Id = 1,
                    Level = "Normal",
                    PeakEquity = 0m,
                    CurrentDrawdownPct = 0m,
                    LastUpdated = DateTimeOffset.UtcNow,
                    LastPeakResetTime = DateTimeOffset.UtcNow,
                    ManualRecoveryRequested = false
                });
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            logger.LogInformation("DrawdownState table created successfully");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
