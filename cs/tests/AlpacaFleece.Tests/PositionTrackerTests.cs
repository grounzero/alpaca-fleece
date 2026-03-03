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
}
