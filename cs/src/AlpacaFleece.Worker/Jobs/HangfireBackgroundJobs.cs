using Hangfire;
using Microsoft.AspNetCore.Builder;

namespace AlpacaFleece.Worker.Jobs;

/// <summary>
/// Hangfire background job configuration and job definitions.
/// Provides recurring jobs for equity snapshots, daily resets, circuit breaker resets.
/// </summary>
public class HangfireBackgroundJobs(IServiceProvider serviceProvider, ILogger<HangfireBackgroundJobs> logger)
{
    /// <summary>
    /// Configures recurring Hangfire jobs.
    /// </summary>
    public void ConfigureRecurringJobs(IRecurringJobManager recurringJobManager)
    {
        // Equity snapshots every 60 seconds
        recurringJobManager.AddOrUpdate(
            "equity-snapshots",
            () => EquitySnapshotJobAsync(),
            Cron.MinuteInterval(1),
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Daily reset at 09:30 ET, weekdays only
        recurringJobManager.AddOrUpdate(
            "daily-reset",
            () => DailyResetJobAsync(),
            "30 14 * * 1-5", // 09:30 ET = 14:30 UTC
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });

        // Circuit breaker reset at 09:30 ET, weekdays only
        recurringJobManager.AddOrUpdate(
            "circuit-breaker-reset",
            () => CircuitBreakerResetJobAsync(),
            "30 14 * * 1-5",
            new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
    }

    /// <summary>
    /// Equity snapshot job: fetches account, persists to equity_curve table.
    /// </summary>
    public async Task EquitySnapshotJobAsync()
    {
        var scope = serviceProvider.CreateAsyncScope();
        try
        {
            var brokerService = scope.ServiceProvider.GetRequiredService<IBrokerService>();
            var stateRepository = scope.ServiceProvider.GetRequiredService<IStateRepository>();

            var account = await brokerService.GetAccountAsync();
            var snapshotTime = DateTimeOffset.UtcNow;

            await stateRepository.InsertEquitySnapshotAsync(
                snapshotTime,
                account.PortfolioValue,
                account.CashAvailable,
                0m,
                CancellationToken.None);

            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug("Hangfire equity snapshot completed: portfolio={portfolio}",
                    account.PortfolioValue);
            }

            // Record result in reconciliation_reports
            await RecordJobResultAsync(stateRepository,
                "equity-snapshot", "SUCCESS", "Snapshot taken");
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
    /// </summary>
    public async Task DailyResetJobAsync()
    {
        var scope = serviceProvider.CreateAsyncScope();
        try
        {
            var stateRepository = scope.ServiceProvider.GetRequiredService<IStateRepository>();

            logger.LogInformation("Daily reset job running");
            await stateRepository.ResetDailyStateAsync();
            await stateRepository.SetStateAsync("daily_reset_date",
                DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"));

            logger.LogInformation("Daily reset job completed");
            await RecordJobResultAsync(stateRepository,
                "daily-reset", "SUCCESS", "Daily state reset");
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
    public async Task CircuitBreakerResetJobAsync()
    {
        var scope = serviceProvider.CreateAsyncScope();
        try
        {
            var stateRepository = scope.ServiceProvider.GetRequiredService<IStateRepository>();
            var brokerService = scope.ServiceProvider.GetRequiredService<IBrokerService>();

            // Only reset if market is open
            var clock = await brokerService.GetClockAsync();
            if (!clock.IsOpen)
            {
                logger.LogWarning("Circuit breaker reset job: market not open, skipping");
                return;
            }

            logger.LogInformation("Circuit breaker reset job running");
            await stateRepository.SaveCircuitBreakerCountAsync(0);

            logger.LogInformation("Circuit breaker reset job completed");
            await RecordJobResultAsync(stateRepository,
                "circuit-breaker-reset", "SUCCESS", "Circuit breaker count reset to 0");
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
        string message)
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
                CancellationToken.None);

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
