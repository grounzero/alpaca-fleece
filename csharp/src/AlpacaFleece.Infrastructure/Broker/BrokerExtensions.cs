namespace AlpacaFleece.Infrastructure.Broker;

/// <summary>
/// Dependency injection extensions for broker services.
/// </summary>
public static class BrokerExtensions
{
    /// <summary>
    /// Registers broker services.
    /// </summary>
    public static IServiceCollection AddBrokerServices(
        this IServiceCollection services,
        BrokerOptions options)
    {
        options.Validate();

        services.AddSingleton(options);
        services.AddSingleton<IBrokerService, AlpacaBrokerService>();

        return services;
    }
}
