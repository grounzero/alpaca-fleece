namespace AlpacaFleece.Infrastructure.EventBus;

/// <summary>
/// Dependency injection extensions for event bus.
/// </summary>
public static class EventBusExtensions
{
    /// <summary>
    /// Registers event bus service.
    /// </summary>
    public static IServiceCollection AddEventBus(this IServiceCollection services)
    {
        services.AddSingleton<IEventBus, EventBusService>();
        return services;
    }
}
