namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Housekeeping service: equity snapshots (60s), daily resets (09:30 ET weekdays).
/// Graceful shutdown: cancel orders → flatten positions → final snapshot.
/// </summary>
public sealed class HousekeepingService(
    IBrokerService brokerService,
    IStateRepository stateRepository,
    IServiceScopeFactory scopeFactory,
    ILogger<HousekeepingService> logger,
    HealthCheckService? healthCheckService = null) : BackgroundService
{
    private const int EquitySnapshotIntervalSeconds = 60;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HousekeepingService starting");

        // Start both tasks concurrently
        var tasks = new[]
        {
            EquitySnapshotsTaskAsync(stoppingToken),
            DailyResetsTaskAsync(stoppingToken)
        };

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("HousekeepingService stopping");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "HousekeepingService error");
        }
    }

    /// <summary>
    /// Graceful shutdown: cancel orders, flatten positions, final snapshot.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Graceful shutdown initiated");

        try
        {
            // Cancel all open orders
            var openOrders = await brokerService.GetOpenOrdersAsync(cancellationToken);
            foreach (var order in openOrders)
            {
                try
                {
                    await brokerService.CancelOrderAsync(order.AlpacaOrderId, cancellationToken);
                    logger.LogInformation("Cancelled order {orderId}", order.AlpacaOrderId);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to cancel order {orderId}", order.AlpacaOrderId);
                }
            }

            // Flatten all positions via OrderManager (deterministic clientOrderId, persist-before-submit)
            using var scope = scopeFactory.CreateScope();
            var orderManager = scope.ServiceProvider.GetRequiredService<IOrderManager>();
            var submitted = await orderManager.FlattenPositionsAsync(cancellationToken);
            logger.LogInformation("Graceful shutdown: flatten submitted {Count} orders", submitted);

            // Final equity snapshot
            await TakeEquitySnapshotAsync(cancellationToken);

            logger.LogInformation("Graceful shutdown complete");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during graceful shutdown");
        }

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Periodically snapshots account equity to equity_curve table.
    /// </summary>
    private async Task EquitySnapshotsTaskAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(EquitySnapshotIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await TakeEquitySnapshotAsync(stoppingToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error taking equity snapshot");
                }
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    /// <summary>
    /// Takes an equity snapshot: fetches account, persists to equity_curve.
    /// </summary>
    private async ValueTask TakeEquitySnapshotAsync(CancellationToken ct)
    {
        try
        {
            var account = await brokerService.GetAccountAsync(ct);
            var snapshotTime = DateTimeOffset.UtcNow;

            // O-1: Read the actual daily realised PnL from the KV store (accumulated by EventDispatcherService on fills).
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

            // O-3: Write health check result to data/health.json if the health check service is registered.
            if (healthCheckService != null)
            {
                try
                {
                    var healthReport = await healthCheckService.CheckHealthAsync(new HealthCheckContext(), ct);
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
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to take equity snapshot");
        }
    }

    /// <summary>
    /// Daily reset task: runs at 09:30 ET weekdays only, resets daily counters.
    /// </summary>
    private async Task DailyResetsTaskAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var nextResetTime = CalculateNextResetTime();
                // C-1: Use UtcNow for delay calculation (nextResetTime is already in UTC).
                var delayMs = (nextResetTime - DateTimeOffset.UtcNow).TotalMilliseconds;

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, stoppingToken);
                }

                try
                {
                    // Check if already reset today (prevent duplicate resets)
                    var lastResetDate = await stateRepository.GetStateAsync(
                        "daily_reset_date", stoppingToken);
                    // C-1: Format the ET-local date, not the server local time.
                    var etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
                    var todayStr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, etZone).ToString("yyyy-MM-dd");

                    if (lastResetDate != todayStr)
                    {
                        logger.LogInformation("Daily reset at {time}", DateTimeOffset.UtcNow);
                        await stateRepository.ResetDailyStateAsync(stoppingToken);
                        await stateRepository.SetStateAsync(
                            "daily_reset_date", todayStr, stoppingToken);
                        logger.LogInformation("Daily reset complete");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Error in daily reset");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    /// <summary>
    /// Calculates next reset time: 09:30 ET next weekday.
    /// </summary>
    private static DateTimeOffset CalculateNextResetTime()
    {
        // C-1: Use IANA timezone ID "America/New_York" for cross-platform compatibility
        // (works on Linux/macOS Docker and Windows; "Eastern Standard Time" is Windows-only).
        var etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var nowEt = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, etZone);

        var nextReset = nowEt.Date.AddHours(9).AddMinutes(30);

        // If already past 09:30 today, schedule for next weekday
        if (nowEt.TimeOfDay >= new TimeSpan(9, 30, 0))
        {
            nextReset = nextReset.AddDays(1);
        }

        // Skip weekends
        while (nextReset.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            nextReset = nextReset.AddDays(1);
        }

        // Convert back to UTC (TimeZoneInfo.ConvertTime handles DST correctly)
        return TimeZoneInfo.ConvertTimeToUtc(nextReset, etZone);
    }
}
