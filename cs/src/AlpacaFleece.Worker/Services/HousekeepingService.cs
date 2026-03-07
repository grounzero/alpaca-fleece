namespace AlpacaFleece.Worker.Services;

/// <summary>
/// Housekeeping service: provides graceful shutdown handling.
/// Graceful shutdown: cancel orders → flatten positions → final snapshot.
/// </summary>
public sealed class HousekeepingService(
    IBrokerService brokerService,
    IStateRepository stateRepository,
    IServiceScopeFactory scopeFactory,
    ILogger<HousekeepingService> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("HousekeepingService registered for graceful shutdown only");
        return Task.CompletedTask;
    }

    /// <summary>
    /// Graceful shutdown: block new signals, cancel orders, flatten positions, final snapshot.
    /// </summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("Graceful shutdown initiated");

        try
        {
            // Block new signals IMMEDIATELY before flattening positions (ExitManager may still dispatch briefly)
            try
            {
                await stateRepository.SetStateAsync("trading_ready", "false", CancellationToken.None);
                logger.LogInformation("HousekeepingService: trading_ready set to false");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to set trading_ready=false on shutdown");
            }

            // Use a longer-lived CancellationTokenSource (60s) for broker operations,
            // since the host shutdown token has a 5-second default timeout and broker calls
            // (cancel all orders, flatten positions) can exceed that
            using var flattenCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var flattenCt = flattenCts.Token;

            // Cancel all open orders
            try
            {
                var openOrders = await brokerService.GetOpenOrdersAsync(flattenCt);
                foreach (var order in openOrders)
                {
                    try
                    {
                        await brokerService.CancelOrderAsync(order.AlpacaOrderId, flattenCt);
                        logger.LogInformation("Cancelled order {orderId}", order.AlpacaOrderId);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to cancel order {orderId}", order.AlpacaOrderId);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error cancelling open orders during shutdown");
            }

            // Flatten all positions via OrderManager (deterministic clientOrderId, persist-before-submit)
            try
            {
                using var scope = scopeFactory.CreateScope();
                var orderManager = scope.ServiceProvider.GetRequiredService<IOrderManager>();
                var submitted = await orderManager.FlattenPositionsAsync(flattenCt);
                logger.LogInformation("Graceful shutdown: flatten submitted {Count} orders", submitted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error flattening positions during shutdown");
            }

            // Final equity snapshot
            try
            {
                var account = await brokerService.GetAccountAsync(flattenCt);
                var dailyPnlStr = await stateRepository.GetStateAsync("daily_realized_pnl", flattenCt);
                // Use InvariantCulture to ensure consistent parsing across locales
                var dailyPnl = decimal.TryParse(dailyPnlStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

                await stateRepository.InsertEquitySnapshotAsync(
                    DateTimeOffset.UtcNow,
                    account.PortfolioValue,
                    account.CashAvailable,
                    dailyPnl,
                    flattenCt);

                logger.LogInformation("Final equity snapshot taken: portfolio={portfolio}, dailyPnl={dailyPnl}",
                    account.PortfolioValue, dailyPnl);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to take final equity snapshot during shutdown");
            }

            logger.LogInformation("Graceful shutdown complete");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error during graceful shutdown");
        }

        await base.StopAsync(cancellationToken);
    }
}
