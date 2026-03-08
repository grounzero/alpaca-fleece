namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for GetStrategyStatsAsync: per-strategy realised PnL and fill count aggregation.
/// Uses the shared SQLite database fixture to test real EF Core queries.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class StrategyAttributionTests(TradingFixture fixture) : IAsyncLifetime
{
    private IStateRepository Repo => fixture.StateRepository;

    // ── lifecycle ──────────────────────────────────────────────────────────

    public Task InitializeAsync() => CleanupAsync();

    public async Task DisposeAsync() => await CleanupAsync();

    private async Task CleanupAsync()
    {
        // Remove test rows inserted by this class to avoid cross-test contamination.
        var db = fixture.DbContext;
        var testSymbols = new[] { "ATTRIB_AAPL", "ATTRIB_MSFT", "ATTRIB_GOOG" };

        db.Trades.RemoveRange(
            db.Trades.Where(t => testSymbols.Contains(t.Symbol)));
        db.OrderIntents.RemoveRange(
            db.OrderIntents.Where(oi => testSymbols.Contains(oi.Symbol)));

        await db.SaveChangesAsync();
    }

    // ── helpers ────────────────────────────────────────────────────────────

    private async Task InsertFilledPairAsync(
        string clientOrderId,
        string symbol,
        string strategyName,
        decimal realizedPnl)
    {
        var db = fixture.DbContext;

        // A filled SELL order intent tagged with the strategy.
        db.OrderIntents.Add(new OrderIntentEntity
        {
            ClientOrderId = clientOrderId,
            Symbol        = symbol,
            Side          = "sell",
            Quantity      = 1m,
            Status        = "filled",
            StrategyName  = strategyName,
            CreatedAt     = DateTimeOffset.UtcNow,
            FilledAt      = DateTimeOffset.UtcNow
        });

        // The matching trade record with realised PnL.
        db.Trades.Add(new TradeEntity
        {
            ClientOrderId    = clientOrderId,
            Symbol           = symbol,
            Side             = "sell",
            InitialQuantity  = 1m,
            FilledQuantity   = 1m,
            AverageEntryPrice = 100m,
            RealizedPnl      = realizedPnl,
            EnteredAt        = DateTimeOffset.UtcNow,
            ExitedAt         = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
    }

    // ── tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStrategyStatsAsync_NoFills_ReturnsEmptyList()
    {
        // No trades inserted for this test → stats should be empty.
        var stats = await Repo.GetStrategyStatsAsync();

        // Filter to our test symbols only (other tests may have inserted data).
        var testStats = stats.Where(s =>
            s.StrategyName is "SMA_ATTRIB_TEST" or "RSI_ATTRIB_TEST").ToList();

        Assert.Empty(testStats);
    }

    [Fact]
    public async Task GetStrategyStatsAsync_SingleStrategy_ReturnsTotals()
    {
        await InsertFilledPairAsync("attr-sma-1", "ATTRIB_AAPL", "SMA_Test", 50m);
        await InsertFilledPairAsync("attr-sma-2", "ATTRIB_AAPL", "SMA_Test", 30m);

        var stats = await Repo.GetStrategyStatsAsync();
        var sma = stats.FirstOrDefault(s => s.StrategyName == "SMA_Test");

        Assert.NotNull(sma);
        Assert.Equal(2,    sma.FillCount);
        Assert.Equal(80m,  sma.RealizedPnl);
    }

    [Fact]
    public async Task GetStrategyStatsAsync_MultipleStrategies_AttributesSeparately()
    {
        await InsertFilledPairAsync("attr-ms-1", "ATTRIB_MSFT", "SMA_Multi", 100m);
        await InsertFilledPairAsync("attr-ms-2", "ATTRIB_MSFT", "SMA_Multi",  50m);
        await InsertFilledPairAsync("attr-ms-3", "ATTRIB_GOOG", "RSI_Multi", -20m);
        await InsertFilledPairAsync("attr-ms-4", "ATTRIB_GOOG", "RSI_Multi",  10m);

        var stats = await Repo.GetStrategyStatsAsync();
        var sma = stats.FirstOrDefault(s => s.StrategyName == "SMA_Multi");
        var rsi = stats.FirstOrDefault(s => s.StrategyName == "RSI_Multi");

        Assert.NotNull(sma);
        Assert.Equal(2,    sma.FillCount);
        Assert.Equal(150m, sma.RealizedPnl);

        Assert.NotNull(rsi);
        Assert.Equal(2,    rsi.FillCount);
        Assert.Equal(-10m, rsi.RealizedPnl);
    }

    [Fact]
    public async Task GetStrategyStatsAsync_OrderedByDescendingPnl()
    {
        await InsertFilledPairAsync("attr-ord-1", "ATTRIB_AAPL", "LowPnlStrat",  10m);
        await InsertFilledPairAsync("attr-ord-2", "ATTRIB_MSFT", "HighPnlStrat", 500m);

        var stats = await Repo.GetStrategyStatsAsync();
        var relevant = stats.Where(s =>
            s.StrategyName is "LowPnlStrat" or "HighPnlStrat").ToList();

        Assert.Equal(2, relevant.Count);
        Assert.Equal("HighPnlStrat", relevant[0].StrategyName);
        Assert.Equal("LowPnlStrat",  relevant[1].StrategyName);
    }

    [Fact]
    public async Task GetStrategyStatsAsync_UnknownStrategyName_GroupedAsUnknown()
    {
        // OrderIntentEntity with no StrategyName → attributed as "Unknown".
        var db = fixture.DbContext;
        const string coid = "attr-null-1";

        db.OrderIntents.Add(new OrderIntentEntity
        {
            ClientOrderId = coid,
            Symbol        = "ATTRIB_AAPL",
            Side          = "sell",
            Quantity      = 1m,
            Status        = "filled",
            StrategyName  = null,               // intentionally null
            CreatedAt     = DateTimeOffset.UtcNow,
            FilledAt      = DateTimeOffset.UtcNow
        });
        db.Trades.Add(new TradeEntity
        {
            ClientOrderId    = coid,
            Symbol           = "ATTRIB_AAPL",
            Side             = "sell",
            InitialQuantity  = 1m,
            FilledQuantity   = 1m,
            AverageEntryPrice = 100m,
            RealizedPnl      = 25m,
            EnteredAt        = DateTimeOffset.UtcNow,
            ExitedAt         = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var stats = await Repo.GetStrategyStatsAsync();
        var unknown = stats.FirstOrDefault(s => s.StrategyName == "Unknown");

        Assert.NotNull(unknown);
        Assert.True(unknown.RealizedPnl >= 25m); // may aggregate with other "Unknown" rows
    }
}
