namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Background service that periodically calls DrawdownMonitor.UpdateAsync().
///
/// On level transitions:
///   Warning   — alert sent; OrderManager applies position multiplier automatically.
///   Halt      — alert sent; RiskManager blocks new positions automatically.
///   Emergency — alert sent; all open positions closed via IOrderManager.FlattenPositionsAsync().
///   Recovery  — alert sent; normal trading resumes.
/// </summary>
public sealed class DrawdownMonitorService(
    DrawdownMonitor drawdownMonitor,
    IServiceProvider serviceProvider,
    AlertNotifier alertNotifier,
    TradingOptions options,
    ILogger<DrawdownMonitorService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Drawdown.Enabled)
        {
            logger.LogInformation("DrawdownMonitorService: disabled via configuration, not starting");
            return;
        }

        // Initialise from database first
        await drawdownMonitor.InitialiseAsync(stoppingToken);

        logger.LogInformation(
            "DrawdownMonitorService starting (interval={interval}s, warning={warn:P0}, halt={halt:P0}, emergency={emg:P0})",
            options.Drawdown.CheckIntervalSeconds,
            options.Drawdown.WarningThresholdPct,
            options.Drawdown.HaltThresholdPct,
            options.Drawdown.EmergencyThresholdPct);

        // IMMEDIATE CHECK: Run once before timer loop to handle startup scenarios
        // where we're already in Halt/Emergency state
        try
        {
            var (initialPrevious, initialCurrent, initialDrawdownPct) = await drawdownMonitor.UpdateAsync(stoppingToken);

            // Only alert on actual transitions
            if (initialPrevious != initialCurrent)
            {
                logger.LogWarning(
                    "DrawdownMonitorService: immediate check detected transition {previous} → {current} at startup (drawdown={pct:P2})",
                    initialPrevious, initialCurrent, initialDrawdownPct);
                await HandleTransitionAsync(initialPrevious, initialCurrent, initialDrawdownPct, stoppingToken);
            }
            // But still handle Emergency state even without a transition (pre-existing Emergency at restart)
            else if (initialCurrent == DrawdownLevel.Emergency)
            {
                logger.LogWarning(
                    "DrawdownMonitorService: immediate check detected pre-existing Emergency at startup (drawdown={pct:P2})",
                    initialDrawdownPct);
                await HandleEmergencyAsync(initialDrawdownPct, stoppingToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DrawdownMonitorService: immediate check failed at startup");
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(options.Drawdown.CheckIntervalSeconds));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var (previous, current, drawdownPct) = await drawdownMonitor.UpdateAsync(stoppingToken);

                    if (previous != current)
                    {
                        await HandleTransitionAsync(previous, current, drawdownPct, stoppingToken);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "DrawdownMonitorService: error in check cycle");
                }
            }
        }
        catch (OperationCanceledException)
        {
            logger.LogInformation("DrawdownMonitorService stopped");
        }
    }

    private async Task HandleTransitionAsync(
        DrawdownLevel previous,
        DrawdownLevel current,
        decimal drawdownPct,
        CancellationToken ct)
    {
        // Alert on transition
        try
        {
            await alertNotifier.DrawdownLevelChangedAsync(previous, current, drawdownPct, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DrawdownMonitorService: failed to send alert for {previous} → {current}", previous, current);
        }

        // Emergency: close all positions immediately
        if (current == DrawdownLevel.Emergency)
        {
            await HandleEmergencyAsync(drawdownPct, ct);
        }
    }

    private async Task HandleEmergencyAsync(decimal drawdownPct, CancellationToken ct)
    {
        logger.LogCritical(
            "DRAWDOWN EMERGENCY: drawdown={pct:P2} — closing all open positions", drawdownPct);
        try
        {
            // Resolve IOrderManager from service provider (scoped service)
            using var scope = serviceProvider.CreateScope();
            var orderManager = scope.ServiceProvider.GetRequiredService<IOrderManager>();
            var count = await orderManager.FlattenPositionsAsync(ct);
            logger.LogWarning(
                "DrawdownMonitorService: emergency flatten submitted {count} exit orders", count);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "DrawdownMonitorService: emergency flatten failed");
        }
    }
}
