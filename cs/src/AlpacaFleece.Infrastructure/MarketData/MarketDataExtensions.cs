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
    /// <param name="services">The service collection.</param>
    /// <param name="options">Broker connection options.</param>
    /// <param name="maxPriceAgeSeconds">
    /// Maximum age in seconds for a price bar returned by GetSnapshotAsync.
    /// Bars older than this threshold cause a MarketDataException (stale price).
    /// 0 disables the check. Default: 300.
    /// </param>
    public static IServiceCollection AddMarketDataServices(
        this IServiceCollection services,
        BrokerOptions options,
        int maxPriceAgeSeconds = 300)
    {
        var environment = options.IsPaperTrading ? Environments.Paper : Environments.Live;
        var secretKey = new SecretKey(options.ApiKey, options.SecretKey);

        services
            .AddSingleton(environment.GetAlpacaDataClient(secretKey))
            .AddSingleton(environment.GetAlpacaCryptoDataClient(secretKey));

        // Use an explicit factory so maxPriceAgeSeconds (a plain int) is passed without
        // polluting the DI container with a primitive registration.
        services.AddSingleton<IMarketDataClient>(sp => new MarketDataClient(
            sp.GetRequiredService<IAlpacaDataClient>(),
            sp.GetRequiredService<IAlpacaCryptoDataClient>(),
            options,
            sp.GetRequiredService<ILogger<MarketDataClient>>(),
            sp.GetRequiredService<ISymbolClassifier>(),
            maxPriceAgeSeconds));

        return services;
    }
}
