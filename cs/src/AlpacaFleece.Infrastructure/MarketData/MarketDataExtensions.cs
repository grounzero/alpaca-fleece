using Alpaca.Markets;
using AlpacaFleece.Infrastructure.Broker;

namespace AlpacaFleece.Infrastructure.MarketData;

/// <summary>
/// DI extensions for market data services.
/// </summary>
public static class MarketDataExtensionss
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

        return services
            .AddSingleton(environment.GetAlpacaDataClient(secretKey))
            .AddSingleton(environment.GetAlpacaCryptoDataClient(secretKey))
            .AddSingleton<IMarketDataClient, MarketDataClient>();
    }
}
