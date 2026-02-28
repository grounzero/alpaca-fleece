namespace AlpacaFleece.Tests;

/// <summary>
/// Integration tests for BarsHandler historical bar backfill on startup.
/// Uses real SQLite to test database interactions.
/// </summary>
public sealed class BarsHandlerTests : IAsyncLifetime
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"bars_test_{Guid.NewGuid():N}.db");
    private TradingDbContext _dbContext = null!;
    private DbContextOptions<TradingDbContext> _options = null!;
    private IDbContextFactory<TradingDbContext> _factory = null!;
    private ILogger<BarsHandler> _logger = null!;

    public async Task InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<TradingDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        _dbContext = new TradingDbContext(_options);
        await _dbContext.Database.EnsureCreatedAsync();

        _factory = new TestDbContextFactory(_options);
        _logger = Substitute.For<ILogger<BarsHandler>>();
    }

    public async Task DisposeAsync()
    {
        _dbContext?.Dispose();
        if (File.Exists(_databasePath))
            File.Delete(_databasePath);
        await Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // Tests
    // ------------------------------------------------------------------

    [Fact]
    public async Task LoadHistoricalBars_EmptyDatabase_LeavesDequeEmpty()
    {
        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            _factory,
            _logger);

        await handler.LoadHistoricalBarsAsync(CancellationToken.None);

        Assert.Equal(0, handler.GetBarCount("AAPL"));
        Assert.Empty(handler.GetBarsForSymbol("AAPL"));
    }

    [Fact]
    public async Task LoadHistoricalBars_WithBars_PopulatesDeque()
    {
        // Seed 10 bars for AAPL
        var baseTime = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 10; i++)
        {
            _dbContext.Bars.Add(new BarEntity
            {
                Symbol = "AAPL",
                Timeframe = "1H",
                Timestamp = baseTime.AddMinutes(i),
                Open = 100m + i,
                High = 101m + i,
                Low = 99m + i,
                Close = 100.5m + i,
                Volume = 1000000L,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            _factory,
            _logger);

        await handler.LoadHistoricalBarsAsync(CancellationToken.None);

        Assert.Equal(10, handler.GetBarCount("AAPL"));
        var bars = handler.GetBarsForSymbol("AAPL");
        Assert.NotEmpty(bars);
        Assert.Equal(10, bars.Count);
        // Verify bars are in chronological order (earliest first after load)
        Assert.Equal(100m, bars[0].O);
        Assert.Equal(100.5m + 9, bars[9].C);
    }

    [Fact]
    public async Task LoadHistoricalBars_CapAt500_WhenMoreBarsExist()
    {
        // Seed 600 bars for MSFT
        var baseTime = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        for (int i = 0; i < 600; i++)
        {
            _dbContext.Bars.Add(new BarEntity
            {
                Symbol = "MSFT",
                Timeframe = "1H",
                Timestamp = baseTime.AddMinutes(i),
                Open = 300m + i,
                High = 301m + i,
                Low = 299m + i,
                Close = 300.5m + i,
                Volume = 2000000L,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            _factory,
            _logger);

        await handler.LoadHistoricalBarsAsync(CancellationToken.None);

        Assert.Equal(500, handler.GetBarCount("MSFT"));
        var bars = handler.GetBarsForSymbol("MSFT");
        Assert.Equal(500, bars.Count);
        // Verify last 500 bars were loaded (most recent first in query, then reversed)
        // The most recent bar should be from minute 599
        Assert.Equal(300m + 599, bars[499].O);
    }

    [Fact]
    public async Task LoadHistoricalBars_MultipleSymbols_LoadsEachIndependently()
    {
        // Seed bars for multiple symbols
        var baseTime = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        // 15 AAPL bars
        for (int i = 0; i < 15; i++)
        {
            _dbContext.Bars.Add(new BarEntity
            {
                Symbol = "AAPL",
                Timeframe = "1H",
                Timestamp = baseTime.AddMinutes(i),
                Open = 100m + i,
                High = 101m + i,
                Low = 99m + i,
                Close = 100.5m + i,
                Volume = 1000000L,
                CreatedAt = DateTime.UtcNow
            });
        }

        // 25 GOOG bars
        for (int i = 0; i < 25; i++)
        {
            _dbContext.Bars.Add(new BarEntity
            {
                Symbol = "GOOG",
                Timeframe = "1H",
                Timestamp = baseTime.AddMinutes(i),
                Open = 150m + i,
                High = 151m + i,
                Low = 149m + i,
                Close = 150.5m + i,
                Volume = 3000000L,
                CreatedAt = DateTime.UtcNow
            });
        }

        await _dbContext.SaveChangesAsync();

        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            _factory,
            _logger);

        await handler.LoadHistoricalBarsAsync(CancellationToken.None);

        Assert.Equal(15, handler.GetBarCount("AAPL"));
        Assert.Equal(25, handler.GetBarCount("GOOG"));
        Assert.Equal(0, handler.GetBarCount("MSFT"));

        var aaplBars = handler.GetBarsForSymbol("AAPL");
        var googBars = handler.GetBarsForSymbol("GOOG");

        Assert.Equal(15, aaplBars.Count);
        Assert.Equal(25, googBars.Count);
    }

    [Fact]
    public async Task LoadHistoricalBars_DbError_DoesNotThrow()
    {
        // Create a factory that throws
        var failingFactory = Substitute.For<IDbContextFactory<TradingDbContext>>();
        failingFactory.CreateDbContextAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<TradingDbContext>(new InvalidOperationException("DB error")));

        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            failingFactory,
            _logger);

        // Should not throw
        await handler.LoadHistoricalBarsAsync(CancellationToken.None);

        // Handler should have empty deques
        Assert.Equal(0, handler.GetBarCount("AAPL"));
        Assert.Empty(handler.GetBarsForSymbol("AAPL"));

        // Logger should have logged the warning (verify underlying Log call)
        _logger.Received().Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception, string>>());
    }

    [Fact]
    public async Task LoadHistoricalBars_LogsCountPerSymbol()
    {
        var baseTime = new DateTime(2025, 1, 1, 10, 0, 0, DateTimeKind.Utc);

        // Seed 5 bars for AAPL
        for (int i = 0; i < 5; i++)
        {
            _dbContext.Bars.Add(new BarEntity
            {
                Symbol = "AAPL",
                Timeframe = "1H",
                Timestamp = baseTime.AddMinutes(i),
                Open = 100m + i,
                High = 101m + i,
                Low = 99m + i,
                Close = 100.5m + i,
                Volume = 1000000L,
                CreatedAt = DateTime.UtcNow
            });
        }
        await _dbContext.SaveChangesAsync();

        var handler = new BarsHandler(
            Substitute.For<IEventBus>(),
            _factory,
            _logger);

        await handler.LoadHistoricalBarsAsync(CancellationToken.None);

        // Verify logging occurred (underlying Log called at Information level)
        _logger.Received().Log(
            LogLevel.Information,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<Exception>(),
            Arg.Any<Func<object, Exception, string>>());
    }
}

/// <summary>
/// Simple test implementation of IDbContextFactory for BarsHandler tests.
/// </summary>
internal sealed class TestDbContextFactory(DbContextOptions<TradingDbContext> options) : IDbContextFactory<TradingDbContext>
{
    public TradingDbContext CreateDbContext()
        => new TradingDbContext(options);

    public ValueTask<TradingDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        => new(CreateDbContext());
}
