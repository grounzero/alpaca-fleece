namespace AlpacaFleece.Infrastructure.MarketData;

/// <summary>
/// DI extensions for market data services.
/// </summary>
public static class MarketDataExtensions
{
    /// <summary>
    /// Registers MarketDataClient in DI.
    /// </summary>
    public static IServiceCollection AddMarketDataServices(
        this IServiceCollection services)
    {
        services.AddSingleton<IMarketDataClient, MarketDataClient>();
        return services;
    }
}
