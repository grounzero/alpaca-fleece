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

        services.AddDbContext<TradingDbContext>(options =>
            options.UseSqlite(connectionString));

        services.AddScoped<IStateRepository, StateRepository>();

        return services;
    }
}
