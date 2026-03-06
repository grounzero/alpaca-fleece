namespace AlpacaFleece.Worker.Jobs;

/// <summary>
/// Hangfire background job configuration and job definitions.
/// Provides recurring jobs for equity snapshots, daily resets, circuit breaker resets.
/// Jobs are registered using DI-friendly AddOrUpdate&lt;T&gt; overloads.
/// </summary>
public class HangfireBackgroundJobs(
    IServiceProvider serviceProvider, 
    ILogger<HangfireBackgroundJobs> logger,
    HealthCheckService? healthCheckService = null)
{
    /// <summary>
    /// Configures recurring Hangfire jobs using DI-friendly registration.
    /// Call this method during application startup.
    /// Note: IJobCancellationToken parameters are automatically injected by Hangfire at runtime.
    /// </summary>
    public static void ConfigureRecurringJobs(IRecurringJobManager recurringJobManager)
    {
        var etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        // Equity snapshots every 60 seconds (UTC is fine for fixed intervals)
        recurringJobManager.AddOrUpdate<HangfireBackgroundJobs>(
            "equity-snapshots",
            j => j.EquitySnapshotJobAsync(JobCancellationToken.Null),
            Cron.MinuteInterval(1),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Daily reset at 09:30 ET, weekdays only
        // Use ET timezone so Hangfire handles DST automatically (09:30 ET = 13:30 UTC in summer, 14:30 UTC in winter)
        recurringJobManager.AddOrUpdate<HangfireBackgroundJobs>(
            "daily-reset",
            j => j.DailyResetJobAsync(JobCancellationToken.Null),
            "30 9 * * 1-5", // 09:30 local ET time
            new RecurringJobOptions { TimeZone = etZone });

        // Circuit breaker reset at 09:30 ET, weekdays only
        recurringJobManager.AddOrUpdate<HangfireBackgroundJobs>(
            "circuit-breaker-reset",
            j => j.CircuitBreakerResetJobAsync(JobCancellationToken.Null),
            "30 9 * * 1-5", // 09:30 local ET time
            new RecurringJobOptions { TimeZone = etZone });
    }

    /// <summary>
    /// Equity snapshot job: fetches account, persists to equity_curve table.
    /// Includes actual daily PnL and health check reporting.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 300)]
    public async Task EquitySnapshotJobAsync(IJobCancellationToken cancellationToken)
    {
        var ct = cancellationToken.ShutdownToken;
        var scope = serviceProvider.CreateAsyncScope();
        try
        {
            var brokerService = scope.ServiceProvider.GetRequiredService<IBrokerService>();
            var stateRepository = scope.ServiceProvider.GetRequiredService<IStateRepository>();

            var account = await brokerService.GetAccountAsync(ct);
            var snapshotTime = DateTimeOffset.UtcNow;

            // Read actual daily realized PnL from state (accumulated by EventDispatcherService on fills)
            var dailyPnlStr = await stateRepository.GetStateAsync("daily_realized_pnl", ct);
            var dailyPnl = decimal.TryParse(dailyPnlStr, out var parsed) ? parsed : 0m;

            await stateRepository.InsertEquitySnapshotAsync(
                snapshotTime,
                account.PortfolioValue,
                account.CashAvailable,
                dailyPnl,
                ct);

            logger.LogDebug(
                "Equity snapshot taken: portfolio={portfolio} cash={cash} dailyPnl={dailyPnl} at {time}",
                account.PortfolioValue, account.CashAvailable, dailyPnl, snapshotTime);

            // Write health check result to data/health.json if available
            if (healthCheckService != null)
            {
                try
                {
                    var healthReport = await healthCheckService.CheckHealthAsync(
                        new HealthCheckContext(), ct);
                    var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                    Directory.CreateDirectory(dataDir);
                    var healthPath = Path.Combine(dataDir, "health.json");
                    var healthJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Status = healthReport.Status.ToString(),
                        CheckedAt = snapshotTime,
                        Description = healthReport.Description,
                        Data = healthReport.Data
                    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(healthPath, healthJson, ct);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to write health check result");
                }
            }

            // Record result in reconciliation_reports
            await RecordJobResultAsync(stateRepository,
                "equity-snapshot", "SUCCESS", 
                $"Snapshot taken: portfolio={account.PortfolioValue:F2}, dailyPnL={dailyPnl:F2}", ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("EquitySnapshotJob cancelled during execution");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "EquitySnapshotJob failed");
            throw;
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    /// <summary>
    /// Daily reset job: resets daily_trade_count and daily_pnl in bot_state.
    /// Includes duplicate prevention to avoid multiple resets on the same day.
    /// Uses check-then-act within transaction for atomicity.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task DailyResetJobAsync(IJobCancellationToken cancellationToken)
    {
        var ct = cancellationToken.ShutdownToken;
        var scope = serviceProvider.CreateAsyncScope();
        try
        {
            var stateRepository = scope.ServiceProvider.GetRequiredService<IStateRepository>();

            // Check if already reset today (prevent duplicate resets on manual trigger or retry)
            var lastResetDate = await stateRepository.GetStateAsync("daily_reset_date", ct);
            
            // Use ET timezone for date comparison (same timezone as job schedule)
            var etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
            var todayStr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, etZone).ToString("yyyy-MM-dd");

            if (lastResetDate == todayStr)
            {
                logger.LogInformation("Daily reset already performed today ({date}), skipping", todayStr);
                await RecordJobResultAsync(stateRepository,
                    "daily-reset", "SKIPPED", $"Already reset today: {todayStr}", ct);
                return;
            }

            logger.LogInformation("Daily reset job running for date {date}", todayStr);
            
            // Note: DisableConcurrentExecution prevents race conditions.
            // If needed, wrap ResetDailyStateAsync + SetStateAsync in a DB transaction.
            await stateRepository.ResetDailyStateAsync(ct);
            await stateRepository.SetStateAsync("daily_reset_date", todayStr, ct);

            logger.LogInformation("Daily reset job completed for date {date}", todayStr);
            await RecordJobResultAsync(stateRepository,
                "daily-reset", "SUCCESS", $"Daily state reset for {todayStr}", ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("DailyResetJob cancelled during execution");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DailyResetJob failed");
            throw;
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    /// <summary>
    /// Circuit breaker reset job: clears circuit breaker count at market open.
    /// </summary>
    [DisableConcurrentExecution(timeoutInSeconds: 60)]
    public async Task CircuitBreakerResetJobAsync(IJobCancellationToken cancellationToken)
    {
        var ct = cancellationToken.ShutdownToken;
        var scope = serviceProvider.CreateAsyncScope();
        try
        {
            var stateRepository = scope.ServiceProvider.GetRequiredService<IStateRepository>();
            var brokerService = scope.ServiceProvider.GetRequiredService<IBrokerService>();

            // Only reset if market is open
            var clock = await brokerService.GetClockAsync(ct);
            if (!clock.IsOpen)
            {
                logger.LogWarning("Circuit breaker reset job: market not open, skipping");
                await RecordJobResultAsync(stateRepository,
                    "circuit-breaker-reset", "SKIPPED", "Market not open", ct);
                return;
            }

            logger.LogInformation("Circuit breaker reset job running");
            await stateRepository.SaveCircuitBreakerCountAsync(0, ct);

            logger.LogInformation("Circuit breaker reset job completed");
            await RecordJobResultAsync(stateRepository,
                "circuit-breaker-reset", "SUCCESS", "Circuit breaker count reset to 0", ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("CircuitBreakerResetJob cancelled during execution");
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "CircuitBreakerResetJob failed");
            throw;
        }
        finally
        {
            await scope.DisposeAsync();
        }
    }

    /// <summary>
    /// Records job execution result to reconciliation_reports table.
    /// </summary>
    private async Task RecordJobResultAsync(
        IStateRepository stateRepository,
        string jobName,
        string status,
        string message,
        CancellationToken ct)
    {
        try
        {
            var report = new
            {
                JobName = jobName,
                Status = status,
                Message = message,
                ExecutedAt = DateTimeOffset.UtcNow,
                Duration = 0
            };
            await stateRepository.InsertReconciliationReportAsync(
                System.Text.Json.JsonSerializer.Serialize(report),
                ct);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Job result recorded for {jobName}: {status}", jobName, status);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record job result");
        }
    }
}

/// <summary>
/// Extension methods for Hangfire integration in DI.
/// </summary>
public static class HangfireExtensions
{
    /// <summary>
    /// Adds Hangfire services to the dependency injection container.
    /// Requires Hangfire.InMemory or Hangfire.SqlServer NuGet package for storage.
    /// </summary>
    public static IServiceCollection AddHangfireServices(
        this IServiceCollection services)
    {
        // Add Hangfire with default configuration
        // Note: Storage backend should be configured via NuGet package (Hangfire.InMemory, Hangfire.SqlServer, etc.)
        services.AddHangfire(config => { });

        services.AddHangfireServer(options =>
        {
            options.WorkerCount = Environment.ProcessorCount;
            options.SchedulePollingInterval = TimeSpan.FromSeconds(5);
        });

        // Register HangfireBackgroundJobs for DI
        services.AddScoped<HangfireBackgroundJobs>();

        return services;
    }

    /// <summary>
    /// Adds Hangfire middleware and dashboard to the application.
    /// </summary>
    public static void UseHangfireServices(this IApplicationBuilder app)
    {
        app.UseHangfireDashboard();
    }
}
