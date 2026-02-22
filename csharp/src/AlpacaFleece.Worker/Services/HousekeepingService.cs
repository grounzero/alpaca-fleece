namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Housekeeping service: equity snapshots (60s), daily resets (09:30 ET weekdays).
/// Graceful shutdown: cancel orders → flatten positions → final snapshot.
/// </summary>
public sealed class HousekeepingService(
    IBrokerService brokerService,
    IStateRepository stateRepository,
    PositionTracker positionTracker,
    IOrderManager orderManager,
    ILogger<HousekeepingService> logger) : BackgroundService
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

            // Flatten all positions (market sell all)
            await FlattenPositionsAsync(cancellationToken);

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

            await stateRepository.InsertEquitySnapshotAsync(
                snapshotTime,
                account.PortfolioValue,
                account.CashAvailable,
                0m, // Daily PnL - would calculate from positions
                ct);

            logger.LogDebug("Equity snapshot taken: portfolio={portfolio} cash={cash} at {time}",
                account.PortfolioValue, account.CashAvailable, snapshotTime);
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
                var delayMs = (nextResetTime - DateTimeOffset.Now).TotalMilliseconds;

                if (delayMs > 0)
                {
                    await Task.Delay((int)delayMs, stoppingToken);
                }

                try
                {
                    // Check if already reset today (prevent duplicate resets)
                    var lastResetDate = await stateRepository.GetStateAsync(
                        "daily_reset_date", stoppingToken);
                    var todayStr = DateTimeOffset.Now.ToString("yyyy-MM-dd");

                    if (lastResetDate != todayStr)
                    {
                        logger.LogInformation("Daily reset at {time}", DateTimeOffset.Now);
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
    /// Flattens all positions: cancel pending orders, sell all shares.
    /// </summary>
    private async ValueTask FlattenPositionsAsync(CancellationToken ct)
    {
        try
        {
            var positions = positionTracker.GetAllPositions();

            foreach (var (symbol, posData) in positions)
            {
                try
                {
                    // Submit market sell
                    var clientOrderId = $"FLATTEN_{symbol}_{Guid.NewGuid():N}"[..40];
                    await brokerService.SubmitOrderAsync(
                        symbol,
                        "SELL",
                        posData.CurrentQuantity,
                        0m, // Market order (limit price 0 means market)
                        clientOrderId,
                        ct);

                    logger.LogInformation("Submitted flatten order for {symbol}: {qty} shares",
                        symbol, posData.CurrentQuantity);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to flatten position {symbol}", symbol);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error flattening positions");
        }
    }

    /// <summary>
    /// Calculates next reset time: 09:30 ET next weekday.
    /// </summary>
    private static DateTimeOffset CalculateNextResetTime()
    {
        var estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var nowEst = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, estZone);

        var nextReset = nowEst.Date.AddHours(9).AddMinutes(30);

        // If already past 09:30 today, schedule for next weekday
        if (nowEst.TimeOfDay >= new TimeSpan(9, 30, 0))
        {
            nextReset = nextReset.AddDays(1);
        }

        // Skip weekends
        while (nextReset.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
        {
            nextReset = nextReset.AddDays(1);
        }

        // Convert back to UTC
        return TimeZoneInfo.ConvertTime(nextReset, estZone, TimeZoneInfo.Utc);
    }
}
