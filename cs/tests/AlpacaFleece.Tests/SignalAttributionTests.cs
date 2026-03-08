namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for signal attribution (Phase 1: multi-strategy foundation).
/// Verifies that signals carry strategy name through event pipeline.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class SignalAttributionTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly TradingFixture _fixture = fixture;

    public async Task InitializeAsync()
    {
        // Clean up any existing test data from previous tests
        var aaplIntents = _fixture.DbContext.OrderIntents.Where(o => o.Symbol == "AAPL").ToList();
        var testIntents = _fixture.DbContext.OrderIntents.Where(o => o.Symbol == "TEST").ToList();
        _fixture.DbContext.OrderIntents.RemoveRange(aaplIntents.Concat(testIntents));
        await _fixture.DbContext.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void SignalEvent_CarriesStrategyName_WhenSet()
    {
        // Arrange
        const string strategyName = "SMA_5x15_10x30_20x50";
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1Min",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 148m,
                SlowSma: 145m,
                Atr: 2.5m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.9m,
                CurrentPrice: 150m),
            StrategyName: strategyName);

        // Act & Assert
        Assert.NotNull(signal.StrategyName);
        Assert.Equal(strategyName, signal.StrategyName);
    }

    [Fact]
    public void SignalEvent_AllowsNullStrategyName_ForBackwardCompatibility()
    {
        // Arrange
        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1Min",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 148m,
                SlowSma: 145m,
                Atr: 2.5m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.9m,
                CurrentPrice: 150m));
        // StrategyName not set — should default to null

        // Act & Assert
        Assert.Null(signal.StrategyName);
    }

    [Fact]
    public void OrderIntentEvent_CarriesStrategyName_FromSignal()
    {
        // Arrange
        const string strategyName = "SMA_5x15_10x30_20x50";
        var intentEvent = new OrderIntentEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Quantity: 100m,
            ClientOrderId: "test-client-123",
            CreatedAt: DateTimeOffset.UtcNow,
            StrategyName: strategyName);

        // Act & Assert
        Assert.Equal(strategyName, intentEvent.StrategyName);
    }

    [Fact]
    public void OrderIntentEvent_AllowsNullStrategyName_ForExitOrders()
    {
        // Arrange
        var exitEvent = new OrderIntentEvent(
            Symbol: "AAPL",
            Side: "SELL",
            Quantity: 100m,
            ClientOrderId: "test-exit-123",
            CreatedAt: DateTimeOffset.UtcNow,
            StrategyName: null);

        // Act & Assert
        Assert.Null(exitEvent.StrategyName);
    }

    [Fact]
    public async Task OrderIntentEntity_PersistsStrategyName_InDatabase()
    {
        // Arrange
        var clientOrderId = Guid.NewGuid().ToString("N")[..16];
        const string strategyName = "SMA_5x15_10x30_20x50";
        var stateRepo = _fixture.StateRepository;

        // Act: Save intent with strategy name
        var saved = await stateRepo.SaveOrderIntentAsync(
            clientOrderId: clientOrderId,
            symbol: "AAPL",
            side: "BUY",
            quantity: 100m,
            limitPrice: 150m,
            createdAt: DateTimeOffset.UtcNow,
            ct: CancellationToken.None,
            atrSeed: null,
            strategyName: strategyName);

        // Assert: Verify insert succeeded
        Assert.True(saved);

        // Retrieve and verify strategy name was persisted
        var intent = await stateRepo.GetOrderIntentAsync(clientOrderId, CancellationToken.None);
        Assert.NotNull(intent);
        Assert.Equal(strategyName, intent.StrategyName);
    }

    [Fact]
    public async Task OrderIntentEntity_AllowsNullStrategyName_ForExitOrders()
    {
        // Arrange
        var clientOrderId = Guid.NewGuid().ToString("N")[..16];
        var stateRepo = _fixture.StateRepository;

        // Act: Save exit intent without strategy name
        var saved = await stateRepo.SaveOrderIntentAsync(
            clientOrderId: clientOrderId,
            symbol: "AAPL",
            side: "SELL",
            quantity: 50m,
            limitPrice: 0m, // Market exit
            createdAt: DateTimeOffset.UtcNow,
            ct: CancellationToken.None,
            atrSeed: null,
            strategyName: null); // Exit orders have no strategy

        // Assert
        Assert.True(saved);

        var intent = await stateRepo.GetOrderIntentAsync(clientOrderId, CancellationToken.None);
        Assert.NotNull(intent);
        Assert.Null(intent.StrategyName);
    }

    [Fact]
    public async Task StrategyName_Idempotent_AcrossMultipleCalls()
    {
        // Arrange
        var clientOrderId = Guid.NewGuid().ToString("N")[..16];
        const string strategyName = "SMA_5x15_10x30_20x50";
        var stateRepo = _fixture.StateRepository;

        // Act: Try to save same intent twice
        var saved1 = await stateRepo.SaveOrderIntentAsync(
            clientOrderId: clientOrderId,
            symbol: "AAPL",
            side: "BUY",
            quantity: 100m,
            limitPrice: 150m,
            createdAt: DateTimeOffset.UtcNow,
            ct: CancellationToken.None,
            atrSeed: null,
            strategyName: strategyName);

        var saved2 = await stateRepo.SaveOrderIntentAsync(
            clientOrderId: clientOrderId,
            symbol: "AAPL",
            side: "BUY",
            quantity: 100m,
            limitPrice: 150m,
            createdAt: DateTimeOffset.UtcNow,
            ct: CancellationToken.None,
            atrSeed: null,
            strategyName: strategyName);

        // Assert
        Assert.True(saved1);  // First insert succeeds
        Assert.False(saved2); // Second insert rejected (already exists)

        // Verify strategy name is unchanged
        var intent = await stateRepo.GetOrderIntentAsync(clientOrderId, CancellationToken.None);
        Assert.NotNull(intent);
        Assert.Equal(strategyName, intent.StrategyName);
    }

    [Fact]
    public async Task StrategyName_CanDifferBetweenSymbols()
    {
        // Arrange
        var clientOrderId1 = Guid.NewGuid().ToString("N")[..16];
        var clientOrderId2 = Guid.NewGuid().ToString("N")[..16];
        const string strategy1 = "SMA_5x15_10x30_20x50";
        const string strategy2 = "Momentum";
        var stateRepo = _fixture.StateRepository;

        // Act: Save two orders with different strategy names
        var saved1 = await stateRepo.SaveOrderIntentAsync(
            clientOrderId: clientOrderId1,
            symbol: "AAPL",
            side: "BUY",
            quantity: 100m,
            limitPrice: 150m,
            createdAt: DateTimeOffset.UtcNow,
            ct: CancellationToken.None,
            atrSeed: null,
            strategyName: strategy1);

        var saved2 = await stateRepo.SaveOrderIntentAsync(
            clientOrderId: clientOrderId2,
            symbol: "AAPL",
            side: "BUY",
            quantity: 50m,
            limitPrice: 150m,
            createdAt: DateTimeOffset.UtcNow.AddSeconds(1),
            ct: CancellationToken.None,
            atrSeed: null,
            strategyName: strategy2);

        // Assert
        Assert.True(saved1);
        Assert.True(saved2);

        var intent1 = await stateRepo.GetOrderIntentAsync(clientOrderId1, CancellationToken.None);
        var intent2 = await stateRepo.GetOrderIntentAsync(clientOrderId2, CancellationToken.None);

        Assert.NotNull(intent1);
        Assert.NotNull(intent2);
        Assert.Equal(strategy1, intent1.StrategyName);
        Assert.Equal(strategy2, intent2.StrategyName);
    }
}
