using System.IO;
using Microsoft.EntityFrameworkCore;
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

        // Use a local sqlite file in the current working directory for design-time operations.
        var file = Path.Combine(Directory.GetCurrentDirectory(), "trading.db");
        optionsBuilder.UseSqlite($"Data Source={file}");

        return new TradingDbContext(optionsBuilder.Options);
    }
}
