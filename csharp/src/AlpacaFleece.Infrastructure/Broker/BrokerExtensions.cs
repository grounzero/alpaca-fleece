using Alpaca.Markets;

namespace AlpacaFleece.Infrastructure.Broker;

/// <summary>
/// Dependency injection extensions for broker services.
/// </summary>
public static class BrokerExtensions
{
    /// <summary>
    /// Registers broker services and creates the Alpaca trading client.
    /// </summary>
    public static IServiceCollection AddBrokerServices(
        this IServiceCollection services,
        BrokerOptions options)
    {
        options.Validate();

        var environment = options.IsPaperTrading ? Environments.Paper : Environments.Live;
        var secretKey = new SecretKey(options.ApiKey, options.SecretKey);
        var tradingClient = environment.GetAlpacaTradingClient(secretKey);

        services.AddSingleton(options);
        services.AddSingleton(tradingClient);
        services.AddSingleton<IBrokerService, AlpacaBrokerService>();

        return services;
    }
}
