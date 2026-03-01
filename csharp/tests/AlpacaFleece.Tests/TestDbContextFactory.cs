using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace AlpacaFleece.Tests;

/// <summary>
/// Simple test implementation of IDbContextFactory for tests.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<TradingDbContext> options) : IDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext()
        => new TradingDbContext(options);

    public ValueTask<TradingDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => new(CreateDbContext());
}
