namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for PositionTracker, specifically InitialiseFromDbAsync (Feature 5).
/// </summary>
[Collection("Trading Database Collection")]
public sealed class PositionTrackerTests(TradingFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task InitialiseFromDbAsync_RehydratesPositionsFromDatabase()
    {
        // Arrange: insert a live position row directly into the DB
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        fixture.DbContext.PositionTracking.Add(new PositionTrackingEntity
        {
            Symbol = symbol,
            CurrentQuantity = 50,
            EntryPrice = 200m,
            AtrValue = 3.5m,
            TrailingStopPrice = 195m,
            LastUpdateAt = DateTimeOffset.UtcNow
        });
        await fixture.DbContext.SaveChangesAsync();

        var logger = Substitute.For<ILogger<PositionTracker>>();
        var tracker = new PositionTracker(fixture.StateRepository, logger);

        // Act
        await tracker.InitialiseFromDbAsync();

        // Assert: in-memory position was loaded from DB
        var pos = tracker.GetPosition(symbol);
        Assert.NotNull(pos);
        Assert.Equal(50, pos.CurrentQuantity);
        Assert.Equal(200m, pos.EntryPrice);
        Assert.Equal(3.5m, pos.AtrValue);
    }

    [Fact]
    public async Task InitialiseFromDbAsync_SkipsRowsWithZeroQuantity()
    {
        // Arrange: insert a closed position (CurrentQuantity = 0)
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        fixture.DbContext.PositionTracking.Add(new PositionTrackingEntity
        {
            Symbol = symbol,
            CurrentQuantity = 0,
            EntryPrice = 100m,
            AtrValue = 2m,
            TrailingStopPrice = 0m,
            LastUpdateAt = DateTimeOffset.UtcNow
        });
        await fixture.DbContext.SaveChangesAsync();

        var logger = Substitute.For<ILogger<PositionTracker>>();
        var tracker = new PositionTracker(fixture.StateRepository, logger);

        // Act
        await tracker.InitialiseFromDbAsync();

        // Assert: closed positions (qty = 0) must not be rehydrated
        var pos = tracker.GetPosition(symbol);
        Assert.Null(pos);
    }

    [Fact]
    public async Task OpenPositionAsync_WritesRowToDb()
    {
        // Arrange
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        var logger = Substitute.For<ILogger<PositionTracker>>();
        var tracker = new PositionTracker(fixture.StateRepository, logger);

        // Act
        await tracker.OpenPositionAsync(symbol, 10m, 200m, 3m);

        // Assert: DB row exists with correct qty
        var rows = await fixture.StateRepository.GetAllPositionTrackingAsync();
        var row = rows.FirstOrDefault(r => r.Symbol == symbol);
        Assert.NotEqual(default, row);
        Assert.Equal(10m, row.Quantity);
        Assert.Equal(200m, row.EntryPrice);
        Assert.Equal(3m, row.AtrValue);
    }

    [Fact]
    public async Task ClosePositionAsync_SetsQtyZeroInDb()
    {
        // Arrange: first open, then close
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        var logger = Substitute.For<ILogger<PositionTracker>>();
        var tracker = new PositionTracker(fixture.StateRepository, logger);
        await tracker.OpenPositionAsync(symbol, 10m, 200m, 3m);

        // Act
        await tracker.ClosePositionAsync(symbol);

        // Assert: DB row has qty = 0
        var rows = await fixture.StateRepository.GetAllPositionTrackingAsync();
        var row = rows.FirstOrDefault(r => r.Symbol == symbol);
        Assert.NotEqual(default, row);
        Assert.Equal(0m, row.Quantity);
    }

    [Fact]
    public async Task InitialiseFromDbAsync_RehydratesRowWrittenByOpenPositionAsync()
    {
        // Arrange: write row via OpenPositionAsync, then create fresh tracker
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        var logger = Substitute.For<ILogger<PositionTracker>>();
        var writer = new PositionTracker(fixture.StateRepository, logger);
        await writer.OpenPositionAsync(symbol, 25m, 150m, 2.5m);

        // Act: rehydrate from DB into a new tracker instance
        var reader = new PositionTracker(fixture.StateRepository, logger);
        await reader.InitialiseFromDbAsync();

        // Assert: position appears in-memory
        var pos = reader.GetPosition(symbol);
        Assert.NotNull(pos);
        Assert.Equal(25m, pos.CurrentQuantity);
        Assert.Equal(150m, pos.EntryPrice);
        Assert.Equal(2.5m, pos.AtrValue);
    }

    [Fact]
    public async Task OpenPositionAsync_UpdatesInMemoryAndDb_Atomically()
    {
        // Arrange
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        var logger = Substitute.For<ILogger<PositionTracker>>();
        var tracker = new PositionTracker(fixture.StateRepository, logger);

        // Act
        await tracker.OpenPositionAsync(symbol, 5m, 300m, 4m);

        // Assert: in-memory and DB agree
        var inMemPos = tracker.GetPosition(symbol);
        Assert.NotNull(inMemPos);
        Assert.Equal(5m, inMemPos.CurrentQuantity);

        var rows = await fixture.StateRepository.GetAllPositionTrackingAsync();
        var dbRow = rows.FirstOrDefault(r => r.Symbol == symbol);
        Assert.NotEqual(default, dbRow);
        Assert.Equal(5m, dbRow.Quantity);
    }

    [Fact]
    public async Task ClosePositionAsync_RemovesFromMemoryAndZerosDb()
    {
        // Arrange
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        var logger = Substitute.For<ILogger<PositionTracker>>();
        var tracker = new PositionTracker(fixture.StateRepository, logger);
        await tracker.OpenPositionAsync(symbol, 8m, 100m, 1m);

        // Act
        await tracker.ClosePositionAsync(symbol);

        // Assert: in-memory position is gone
        Assert.Null(tracker.GetPosition(symbol));

        // Assert: DB row has qty = 0 (not deleted)
        var rows = await fixture.StateRepository.GetAllPositionTrackingAsync();
        var dbRow = rows.FirstOrDefault(r => r.Symbol == symbol);
        Assert.NotEqual(default, dbRow);
        Assert.Equal(0m, dbRow.Quantity);
    }
}
