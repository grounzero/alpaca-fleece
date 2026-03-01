using Microsoft.EntityFrameworkCore.Design;

namespace AlpacaFleece.Infrastructure.Data;

/// <summary>
/// Design-time factory for EF Core tools. This avoids constructing the full
/// application service provider when running `dotnet ef` commands.
/// </summary>
public sealed class TradingDbContextFactory : IDesignTimeDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<TradingDbContext>();

        // Use the runtime base directory so EF tooling targets the same DB file the
        // application uses at runtime (AppDomain.CurrentDomain.BaseDirectory/trading.db).
        var runtimeBase = AppDomain.CurrentDomain.BaseDirectory ?? Directory.GetCurrentDirectory();
        var file = Path.Combine(runtimeBase, "trading.db");
        optionsBuilder.UseSqlite($"Data Source={file}");

        return new TradingDbContext(optionsBuilder.Options);
    }
}
