namespace AlpacaFleece.Infrastructure.Repositories;

/// <summary>
/// Dependency injection extensions for state repository.
/// </summary>
public static class StateRepositoryExtensions
{
    /// <summary>
    /// Registers state repository and DbContext.
    /// </summary>
    public static IServiceCollection AddStateRepository(
        this IServiceCollection services,
        string databasePath)
    {
        var connectionString = $"Data Source={databasePath}";

        // Use factory to create DbContext instances per call. Do not register a long-lived
        // DbContext to avoid concurrent usage across threads. Consumers should use
        // IDbContextFactory<TradingDbContext> for short-lived contexts in background services.
        // Note: Using non-pooled factory for SQLite to avoid connection reuse threading issues.
        services.AddDbContextFactory<TradingDbContext>(options =>
            options.UseSqlite(connectionString));

        // StateRepository uses the pooled factory internally and is safe to register as singleton.
        services.AddSingleton<IStateRepository, StateRepository>();

        return services;
    }
}
