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
    /// Configures data feed based on BrokerOptions.DataFeed (SIP or IEX).
    /// </summary>
    public static IServiceCollection AddMarketDataServices(
        this IServiceCollection services,
        BrokerOptions options)
    {
        var environment = options.IsPaperTrading ? Environments.Paper : Environments.Live;
        var secretKey = new SecretKey(options.ApiKey, options.SecretKey);

        // Configure data feed (SIP or IEX)
        var feed = options.DataFeed?.ToUpperInvariant() switch
        {
            "IEX" => DataFeed.Iex,
            "SIP" => DataFeed.Sip,
            _ => DataFeed.Sip  // Default to SIP for backward compatibility
        };

        // Log feed selection for paper trading
        if (options.IsPaperTrading && feed == DataFeed.Sip)
        {
            Console.WriteLine("WARNING: Using SIP feed with paper trading. If you get 'subscription does not permit querying recent SIP data' errors, set Broker:DataFeed=IEX in configuration.");
        }

        var dataClient = environment.GetAlpacaDataClient(secretKey, feed);
        services.AddSingleton<IAlpacaDataClient>(dataClient);
        services.AddSingleton(environment.GetAlpacaCryptoDataClient(secretKey));
        services.AddSingleton<IMarketDataClient, MarketDataClient>();

        return services;
    }
}
