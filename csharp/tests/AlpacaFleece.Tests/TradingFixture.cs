namespace AlpacaFleece.Tests;

/// <summary>
/// Test fixture providing database and services for integration tests.
/// </summary>
public sealed class TradingFixture : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"trading_{Guid.NewGuid():N}.db");
    public TradingDbContext DbContext { get; private set; } = null!;
    public IStateRepository StateRepository { get; private set; } = null!;
    public IEventBus EventBus { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        DbContext = new TradingDbContext(options);
        await DbContext.Database.EnsureCreatedAsync();

        StateRepository = new StateRepository(DbContext, Substitute.For<ILogger<StateRepository>>());
        EventBus = new EventBusService();
    }

    public async Task DisposeAsync()
    {
        DbContext?.Dispose();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
        await Task.CompletedTask;
    }
}

/// <summary>
/// xUnit collection fixture for shared database.
/// </summary>
[CollectionDefinition("Trading Database Collection")]
public sealed class TradingDatabaseCollection : ICollectionFixture<TradingFixture>
{
}
