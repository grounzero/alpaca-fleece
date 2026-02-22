namespace AlpacaFleece.Worker.Health;

/// <summary>
/// Health check service: checks database, broker, circuit breaker, event bus.
/// Returns HealthStatus: Healthy, Degraded, Unhealthy.
/// Endpoint: GET /healthz (JSON).
/// </summary>
public sealed class HealthCheckService(
    IBrokerService brokerService,
    IStateRepository stateRepository,
    ILogger<HealthCheckService> logger) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var data = new Dictionary<string, object>();
        var status = HealthStatus.Healthy;

        try
        {
            // Check database connectivity
            var dbHealthy = await CheckDatabaseAsync(cancellationToken);
            data["database"] = dbHealthy ? "Healthy" : "Unhealthy";
            if (!dbHealthy) status = HealthStatus.Unhealthy;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database health check failed");
            data["database"] = "Error";
            status = HealthStatus.Unhealthy;
        }

        try
        {
            // Check broker connectivity
            var brokerHealthy = await CheckBrokerAsync(cancellationToken);
            data["broker"] = brokerHealthy ? "Healthy" : "Unhealthy";
            if (!brokerHealthy) status = HealthStatus.Degraded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Broker health check failed");
            data["broker"] = "Error";
            status = HealthStatus.Degraded;
        }

        try
        {
            // Check circuit breaker status
            var cbCount = await stateRepository.GetCircuitBreakerCountAsync(cancellationToken);
            data["circuitBreaker"] = cbCount == 0 ? "OK" : $"{cbCount} failures";
            if (cbCount > 10) status = HealthStatus.Degraded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Circuit breaker health check failed");
            data["circuitBreaker"] = "Error";
        }

        try
        {
            // Check event bus health (check trading_halted state)
            var tradingHalted = await stateRepository.GetStateAsync("trading_halted", cancellationToken);
            data["eventBus"] = string.IsNullOrEmpty(tradingHalted) || tradingHalted == "false"
                ? "Healthy"
                : "Degraded";

            if (tradingHalted == "true")
                status = HealthStatus.Degraded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Event bus health check failed");
            data["eventBus"] = "Error";
        }

        logger.LogInformation("Health check: {status}", status);
        return new HealthCheckResult(status, description: "AlpacaFleece health status", data: data);
    }

    private async ValueTask<bool> CheckDatabaseAsync(CancellationToken ct)
    {
        try
        {
            // Try a simple state query
            _ = await stateRepository.GetStateAsync("health_check", ct);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async ValueTask<bool> CheckBrokerAsync(CancellationToken ct)
    {
        try
        {
            // Try to get market clock
            var clock = await brokerService.GetClockAsync(ct);
            return clock != null;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Health check result model.
/// </summary>
public sealed class HealthCheckResult(
    HealthStatus status,
    string? description = null,
    IDictionary<string, object>? data = null)
{
    public HealthStatus Status { get; } = status;
    public string? Description { get; } = description;
    public IDictionary<string, object>? Data { get; } = data;
}

/// <summary>
/// Health status enum.
/// </summary>
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy
}

/// <summary>
/// Health check context (dummy).
/// </summary>
public sealed class HealthCheckContext;

/// <summary>
/// IHealthCheck interface.
/// </summary>
public interface IHealthCheck
{
    Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default);
}
