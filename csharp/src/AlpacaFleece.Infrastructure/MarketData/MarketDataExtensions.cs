using Alpaca.Markets;
using AlpacaFleece.Infrastructure.Broker;

namespace AlpacaFleece.Infrastructure.MarketData;

/// <summary>
/// DI extensions for market data services.
/// </summary>
public static class MarketDataExtensions
{
    /// <summary>
    /// Registers MarketDataClient and Alpaca data clients in DI.
    /// </summary>
    public static IServiceCollection AddMarketDataServices(
        this IServiceCollection services,
        BrokerOptions options)
    {
        var environment = options.IsPaperTrading ? Environments.Paper : Environments.Live;
        var secretKey = new SecretKey(options.ApiKey, options.SecretKey);

        services.AddSingleton(environment.GetAlpacaDataClient(secretKey));
        services.AddSingleton(environment.GetAlpacaCryptoDataClient(secretKey));
        services.AddSingleton<IMarketDataClient, MarketDataClient>();

        return services;
    }
}
