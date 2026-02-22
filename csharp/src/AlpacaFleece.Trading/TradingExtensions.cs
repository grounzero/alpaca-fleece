namespace AlpacaFleece.Trading;

/// <summary>
/// DI extensions for trading services.
/// </summary>
public static class TradingExtensions
{
    /// <summary>
    /// Adds exit manager to DI container.
    /// </summary>
    public static IServiceCollection AddExitManager(
        this IServiceCollection services,
        IOptions<ExitOptions> options)
    {
        services.AddSingleton(sp =>
            new ExitManager(
                sp.GetRequiredService<PositionTracker>(),
                sp.GetRequiredService<IBrokerService>(),
                sp.GetRequiredService<IMarketDataClient>(),
                sp.GetRequiredService<IEventBus>(),
                sp.GetRequiredService<IStateRepository>(),
                sp.GetRequiredService<ILogger<ExitManager>>(),
                options));

        return services;
    }
}
