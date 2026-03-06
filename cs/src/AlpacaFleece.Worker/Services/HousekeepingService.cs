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
            try
            {
                var account = await brokerService.GetAccountAsync(cancellationToken);
                var dailyPnlStr = await stateRepository.GetStateAsync("daily_realized_pnl", cancellationToken);
                // Use InvariantCulture to ensure consistent parsing across locales
                var dailyPnl = decimal.TryParse(dailyPnlStr, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0m;

                await stateRepository.InsertEquitySnapshotAsync(
                    DateTimeOffset.UtcNow,
                    account.PortfolioValue,
                    account.CashAvailable,
                    dailyPnl,
                    cancellationToken);

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
