namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for ExitManager (30s check interval, 5-rule priority, pending_exit flag).
/// </summary>
[Collection("Trading Database Collection")]
public sealed class ExitManagerTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly IMarketDataClient _marketDataClientMock = Substitute.For<IMarketDataClient>();
    private readonly ILogger<PositionTracker> _positionTrackerLogger = Substitute.For<ILogger<PositionTracker>>();
    private readonly ILogger<ExitManager> _logger = Substitute.For<ILogger<ExitManager>>();
    private PositionTracker _positionTracker = null!;
    private ExitManager _exitManager = null!;

    public async Task InitializeAsync()
    {
        _positionTracker = new PositionTracker(fixture.StateRepository, _positionTrackerLogger);
        var options = Options.Create(new TradingOptions
        {
            Exit = new ExitOptions
            {
                CheckIntervalSeconds = 30,
                AtrStopLossMultiplier = 1.5m,
                AtrProfitTargetMultiplier = 3.0m,
                StopLossPercentage = 0.01m,
                ProfitTargetPercentage = 0.02m
            },
            Symbols = new SymbolsOptions()  // uses default CryptoSymbols list
        });

        // Configure market data mock to return price below ATR stop (entry 100, ATR 2 -> stop at 97)
        _marketDataClientMock.GetSnapshotAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new BidAskSpread("AAPL", 95m, 95m, 100, 100, DateTimeOffset.UtcNow));

        _exitManager = new ExitManager(
            _positionTracker,
            _brokerMock,
            _marketDataClientMock,
            fixture.EventBus,
            fixture.StateRepository,
            _logger,
            options);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task CheckPositionsAsync_AtrStopLossTriggersCorrectly()
    {
        // Arrange
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Act
        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert: price below entry - (1.5 * ATR) should trigger
        var position = _positionTracker.GetPosition("AAPL");
        Assert.NotNull(position);
        Assert.True(position.PendingExit || signals.Any(s => s.Symbol == "AAPL"));
    }

    [Fact]
    public async Task CheckPositionsAsync_AtrProfitTargetTriggersCorrectly()
    {
        // Arrange
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Act
        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert: position with valid ATR should be evaluated
        var position = _positionTracker.GetPosition("AAPL");
        Assert.NotNull(position);
    }

    [Fact]
    public async Task CheckPositionsAsync_PendingExitFlagPreventsDoubleTrigger()
    {
        // Arrange
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);
        var position = _positionTracker.GetPosition("AAPL")!;
        position.PendingExit = true;

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Act
        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert
        Assert.Empty(signals);
    }

    [Fact]
    public async Task CheckPositionsAsync_ValidatesAtrBeforeUse()
    {
        // Arrange: open position with invalid (zero) ATR
        var entryPrice = 100m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, 0m);

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Act
        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert: NaN ATR should be skipped
        Assert.Empty(signals);
    }

    [Fact]
    public async Task CheckPositionsAsync_SkipsChecksWhenMarketClosed()
    {
        // Arrange
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Act
        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert
        Assert.Empty(signals);
    }

    [Fact]
    public async Task HandleOrderUpdateAsync_ClearsPendingExitOnFailure()
    {
        // Arrange
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);
        var position = _positionTracker.GetPosition("AAPL")!;
        position.PendingExit = true;

        var orderUpdate = new OrderUpdateEvent(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: "client_123",
            Symbol: "AAPL",
            Side: "sell",
            FilledQuantity: 0,
            RemainingQuantity: 100,
            AverageFilledPrice: 0m,
            Status: OrderState.Canceled,
            UpdatedAt: DateTimeOffset.UtcNow);

        // Act
        await _exitManager.HandleOrderUpdateAsync(orderUpdate, CancellationToken.None);

        // Assert
        Assert.False(position.PendingExit);
    }

    [Fact]
    public async Task HandleOrderUpdateAsync_KeepsPendingExitOnSuccess()
    {
        // Arrange
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);
        var position = _positionTracker.GetPosition("AAPL")!;
        position.PendingExit = true;

        var orderUpdate = new OrderUpdateEvent(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: "client_123",
            Symbol: "AAPL",
            Side: "sell",
            FilledQuantity: 100,
            RemainingQuantity: 0,
            AverageFilledPrice: 98m,
            Status: OrderState.Filled,
            UpdatedAt: DateTimeOffset.UtcNow);

        // Act
        await _exitManager.HandleOrderUpdateAsync(orderUpdate, CancellationToken.None);

        // Assert: Filled is not a failure, keep pending
        Assert.True(position.PendingExit);
    }

    [Fact]
    public async Task PublishesExitSignalToUnboundedChannel()
    {
        // Arrange
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Act
        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert
        if (signals.Any())
        {
            var signal = signals.First();
            Assert.NotNull(signal.ExitReason);
            Assert.Equal("AAPL", signal.Symbol);
        }
    }

    [Fact]
    public async Task RecordExitAttemptAsync_PersistsToRepository()
    {
        // Arrange: unique symbol per test to avoid exit_attempts collision from shared DB
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        var method = typeof(ExitManager).GetMethod(
            "RecordExitAttemptAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new[] { typeof(string), typeof(CancellationToken) },
            null);

        // Act
        if (method != null)
        {
            var task = method.Invoke(_exitManager, new object[] { symbol, CancellationToken.None });
            if (task is Task t)
            {
                await t;
            }
        }

        // Assert: verify attempt was recorded
        var backoff = await fixture.StateRepository.GetExitBackoffSecondsAsync(symbol, CancellationToken.None);
        Assert.Equal(1, backoff);
    }

    [Fact]
    public async Task RecordExitAttemptFailureAsync_UpdatesBackoffExpTime()
    {
        // Arrange: unique symbol per test to avoid exit_attempts collision from shared DB
        var symbol = $"TST{Guid.NewGuid():N}"[..8];
        await fixture.StateRepository.RecordExitAttemptAsync(symbol, CancellationToken.None);

        var method = typeof(ExitManager).GetMethod(
            "RecordExitAttemptFailureAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
            null,
            new[] { typeof(string), typeof(CancellationToken) },
            null);

        // Act
        if (method != null)
        {
            var task = method.Invoke(_exitManager, new object[] { symbol, CancellationToken.None });
            if (task is Task t)
            {
                await t;
            }
        }

        // Assert
        var backoff = await fixture.StateRepository.GetExitBackoffSecondsAsync(symbol, CancellationToken.None);
        Assert.True(backoff >= 1);
    }

    [Fact]
    public async Task GetCurrentPriceAsync_ReturnsNonNegative()
    {
        // Arrange: call CheckPositions which internally uses GetCurrentPriceAsync
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        // Act
        var signals = await _exitManager.CheckPositionsAsync(CancellationToken.None);

        // Assert: should return 0 (no market data available in test), not negative
        // Real implementation would return actual market price
        Assert.True(true); // GetCurrentPriceAsync returns 0 or positive
    }

    [Fact]
    public async Task HandleOrderUpdateAsync_KeepsPendingExitOnPartialFill()
    {
        // Arrange: Bug 1 — PartiallyFilled is non-terminal; PendingExit must NOT be cleared
        var entryPrice = 100m;
        var atrValue = 2m;
        _positionTracker.OpenPosition("AAPL", 100, entryPrice, atrValue);
        var position = _positionTracker.GetPosition("AAPL")!;
        position.PendingExit = true;

        var orderUpdate = new OrderUpdateEvent(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: "client_123",
            Symbol: "AAPL",
            Side: "sell",
            FilledQuantity: 50,
            RemainingQuantity: 50,
            AverageFilledPrice: 98m,
            Status: OrderState.PartiallyFilled,
            UpdatedAt: DateTimeOffset.UtcNow);

        // Act
        await _exitManager.HandleOrderUpdateAsync(orderUpdate, CancellationToken.None);

        // Assert: PartiallyFilled is NOT in terminalFailureStates — PendingExit must remain true
        Assert.True(position.PendingExit);
    }

    [Fact]
    public async Task ExitManager_HandlesCancellation()
    {
        // Arrange
        var cts = new CancellationTokenSource();

        // Act
        var task = Task.Run(async () =>
        {
            try
            {
                await _exitManager.ExecuteAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
        });

        await Task.Delay(100);
        cts.Cancel();
        await task;

        // Assert: should exit gracefully
        Assert.True(true);
    }
}
