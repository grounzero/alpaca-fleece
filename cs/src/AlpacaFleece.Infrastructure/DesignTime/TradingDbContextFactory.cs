using Microsoft.EntityFrameworkCore.Design;

namespace AlpacaFleece.Infrastructure.DesignTime;

/// <summary>
/// Design-time factory for EF Core tools. This avoids constructing the full
/// application service provider when running `dotnet ef` commands.
/// </summary>
public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    /// <summary>
    /// Creates a new TradingDbContext instance for design-time operations (migrations).
    /// </summary>
    /// <param name="args">Command-line arguments (unused for this implementation).</param>
    /// <returns>A configured TradingDbContext instance.</returns>
    public TradingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TradingDbContext>();

        // Use the runtime base directory so EF tooling targets the same DB file the
        // application uses at runtime (AppDomain.CurrentDomain.BaseDirectory/data/trading.db).
        var runtimeBase = AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory();
        var file = Path.Combine(runtimeBase, "data", "trading.db");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        optionsBuilder.UseSqlite($"Data Source={file}");

        return new TradingDbContext(optionsBuilder.Options);
    }
}
