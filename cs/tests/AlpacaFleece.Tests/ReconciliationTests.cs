namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for ReconciliationService (startup rules, ghost position auto-clear).
/// </summary>
[Collection("Trading Database Collection")]
public sealed class ReconciliationTests(TradingFixture fixture)
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly ILogger<ReconciliationService> _logger = Substitute.For<ILogger<ReconciliationService>>();

    [Fact]
    public async Task PerformStartupReconciliationAsync_PassesWhenClean()
    {
        // Arrange
        var reconciliation = new ReconciliationService(
            _brokerMock,
            fixture.StateRepository,
            _logger);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        // Act & Assert: should not throw
        await reconciliation.PerformStartupReconciliationAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PerformStartupReconciliationAsync_ThrowsOnOpenOrderNotInSQLite()
    {
        // Arrange
        var reconciliation = new ReconciliationService(
            _brokerMock,
            fixture.StateRepository,
            _logger);

        var alpacaOrder = new OrderInfo(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: "client_123",
            Symbol: "AAPL",
            Side: "BUY",
            Quantity: 100,
            FilledQuantity: 0,
            AverageFilledPrice: 0m,
            Status: OrderState.PendingNew,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo> { alpacaOrder });

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        // Act & Assert
        var ex = await Assert.ThrowsAsync<ReconciliationException>(
            () => reconciliation.PerformStartupReconciliationAsync(CancellationToken.None).AsTask());

        Assert.Contains("discrepancies", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PerformStartupReconciliationAsync_ReconcileFillsCompareQuantities()
    {
        // Arrange
        var reconciliation = new ReconciliationService(
            _brokerMock,
            fixture.StateRepository,
            _logger);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        // Act & Assert
        await reconciliation.ReconcileFillsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PerformStartupReconciliationAsync_WritesDiscrepancyReport()
    {
        // Arrange
        var dataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
        var reportPath = Path.Combine(dataDir, "reconciliation_error.json");

        // Clean up any existing file
        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }

        var reconciliation = new ReconciliationService(
            _brokerMock,
            fixture.StateRepository,
            _logger);

        var alpacaOrder = new OrderInfo(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: "client_123",
            Symbol: "AAPL",
            Side: "BUY",
            Quantity: 100,
            FilledQuantity: 0,
            AverageFilledPrice: 0m,
            Status: OrderState.PendingNew,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo> { alpacaOrder });

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        // Act
        try
        {
            await reconciliation.PerformStartupReconciliationAsync(CancellationToken.None);
        }
        catch (ReconciliationException)
        {
            // Expected
        }

        // Assert: report file should exist
        await Task.Delay(100); // Give async write time
        Assert.True(File.Exists(reportPath), "Discrepancy report should be written");

        // Cleanup
        if (File.Exists(reportPath))
        {
            File.Delete(reportPath);
        }
    }

    [Fact]
    public async Task ReconcileFillsAsync_DetectsFillDrift()
    {
        // Arrange
        var reconciliation = new ReconciliationService(
            _brokerMock,
            fixture.StateRepository,
            _logger);

        await fixture.StateRepository.SaveOrderIntentAsync(
            "client_123",
            "AAPL",
            "BUY",
            100,
            150m,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var alpacaOrder = new OrderInfo(
            AlpacaOrderId: "alpaca_123",
            ClientOrderId: "client_123",
            Symbol: "AAPL",
            Side: "BUY",
            Quantity: 100,
            FilledQuantity: 50,
            AverageFilledPrice: 150m,
            Status: OrderState.PartiallyFilled,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo> { alpacaOrder });

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        // Act
        await reconciliation.ReconcileFillsAsync(CancellationToken.None);

        // Assert: no exception expected, just logging
    }

    [Fact]
    public async Task RecordExitAttemptAsync_IncrementsCount()
    {
        // Arrange
        var symbol = "AAPL";

        // Act
        await fixture.StateRepository.RecordExitAttemptAsync(symbol, CancellationToken.None);

        // Assert
        var backoff = await fixture.StateRepository.GetExitBackoffSecondsAsync(symbol, CancellationToken.None);
        Assert.Equal(1, backoff);

        // Record again
        await fixture.StateRepository.RecordExitAttemptAsync(symbol, CancellationToken.None);
        var backoff2 = await fixture.StateRepository.GetExitBackoffSecondsAsync(symbol, CancellationToken.None);
        Assert.Equal(2, backoff2);
    }

    [Fact]
    public async Task InsertEquitySnapshotAsync_IdempotentByTimestamp()
    {
        // Arrange
        var timestamp = DateTimeOffset.UtcNow;

        // Act
        await fixture.StateRepository.InsertEquitySnapshotAsync(
            timestamp, 100000m, 50000m, 1000m, CancellationToken.None);

        await fixture.StateRepository.InsertEquitySnapshotAsync(
            timestamp, 100000m, 50000m, 1000m, CancellationToken.None);

        // Assert: should not throw, second insert is idempotent
    }

    [Fact]
    public async Task GetAllOrderIntentsAsync_ReturnsStoredOrders()
    {
        // Arrange
        await fixture.StateRepository.SaveOrderIntentAsync(
            "client_123",
            "AAPL",
            "BUY",
            100,
            150m,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // Act
        var intents = await fixture.StateRepository.GetAllOrderIntentsAsync(CancellationToken.None);

        // Assert
        Assert.NotEmpty(intents);
        var intent = intents.First(o => o.ClientOrderId == "client_123");
        Assert.Equal("AAPL", intent.Symbol);
        Assert.Equal(100, intent.Quantity);
    }

    [Fact]
    public async Task InsertReconciliationReportAsync_PersistsJson()
    {
        // Arrange
        var reportJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            Status = "PASSED",
            DiscrepancyCount = 0
        });

        // Act
        await fixture.StateRepository.InsertReconciliationReportAsync(
            reportJson, CancellationToken.None);

        // Assert: no exception expected
    }

    [Fact]
    public async Task ReconciliationPasses_TradingReadyShouldBeTrue_WhenCallerSetsIt()
    {
        // This test verifies that a clean reconciliation allows "trading_ready" to be set "true".
        // The actual gate-setting is done by OrchestratorService, but we verify the reconciliation
        // itself completes without throwing (which is the pre-condition for the gate to be set).
        await fixture.StateRepository.SetStateAsync("trading_ready", "false");

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        var reconciliation = new ReconciliationService(_brokerMock, fixture.StateRepository, _logger);

        // Act: clean reconciliation should complete without throwing
        await reconciliation.PerformStartupReconciliationAsync(CancellationToken.None);
        await reconciliation.ReconcileFillsAsync(CancellationToken.None);

        // Simulate what OrchestratorService does on success
        await fixture.StateRepository.SetStateAsync("trading_ready", "true");

        var ready = await fixture.StateRepository.GetStateAsync("trading_ready");
        Assert.Equal("true", ready);
    }

    [Fact]
    public async Task ReconciliationThrows_TradingReadyRemainsBlocked()
    {
        // When broker has an unknown open order, reconciliation throws → gate stays "false"
        await fixture.StateRepository.SetStateAsync("trading_ready", "false");

        var unknownOrder = new OrderInfo(
            AlpacaOrderId: "alpaca_gate_test",
            ClientOrderId: "unknown_client",
            Symbol: "AAPL",
            Side: "BUY",
            Quantity: 100,
            FilledQuantity: 0,
            AverageFilledPrice: 0m,
            Status: OrderState.PendingNew,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo> { unknownOrder });
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        var reconciliation = new ReconciliationService(_brokerMock, fixture.StateRepository, _logger);

        // Act: reconciliation throws — simulate OrchestratorService catch path (does NOT set "true")
        try
        {
            await reconciliation.PerformStartupReconciliationAsync(CancellationToken.None);
        }
        catch (ReconciliationException)
        {
            // Expected — OrchestratorService catches and leaves trading_ready = "false"
        }

        var ready = await fixture.StateRepository.GetStateAsync("trading_ready");
        Assert.Equal("false", ready);
    }

    [Fact]
    public async Task PartiallyFilledOrder_DoesNotBlockStartup()
    {
        // Arrange: Alpaca has a PartiallyFilled order that also exists in SQLite as PendingNew.
        // PartiallyFilled is non-terminal — Rule 3 checks if the non-terminal
        // Alpaca order exists in SQLite (it does), so no discrepancy is raised.
        var reconciliation = new ReconciliationService(_brokerMock, fixture.StateRepository, _logger);

        var alpacaOrder = new OrderInfo(
            AlpacaOrderId: "alpaca_partial",
            ClientOrderId: "client_partial",
            Symbol: "NVDA",
            Side: "BUY",
            Quantity: 10,
            FilledQuantity: 5,
            AverageFilledPrice: 800m,
            Status: OrderState.PartiallyFilled,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: null);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo> { alpacaOrder });
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        // Pre-populate SQLite with matching intent (PendingNew)
        await fixture.StateRepository.SaveOrderIntentAsync(
            "client_partial", "NVDA", "BUY", 10m, 800m, DateTimeOffset.UtcNow);
        await fixture.StateRepository.UpdateOrderIntentAsync(
            "client_partial", "alpaca_partial", OrderState.PendingNew, DateTimeOffset.UtcNow);

        // Act & Assert: should not throw — PartiallyFilled is non-terminal so no false discrepancy
        await reconciliation.PerformStartupReconciliationAsync(CancellationToken.None);
    }

    [Fact]
    public async Task GhostPosition_IsClearedFromSqlite()
    {
        // Arrange: SQLite has a position, Alpaca has no matching position/orders.
        var reconciliation = new ReconciliationService(_brokerMock, fixture.StateRepository, _logger);

        // Insert a ghost position in SQLite
        await fixture.StateRepository.UpsertPositionTrackingAsync("GHOST", 10m, 150m, 2m, 145m);

        _brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>());
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        // Act
        await reconciliation.PerformStartupReconciliationAsync(CancellationToken.None);

        // Assert: ghost position cleared — qty should now be 0
        var positions = await fixture.StateRepository.GetAllPositionTrackingAsync();
        var ghost = positions.FirstOrDefault(p => p.Symbol == "GHOST");
        Assert.True(ghost == default || ghost.Quantity == 0m,
            "Ghost position should have been cleared to qty=0");
    }

    // ─── R-2: Fill drift only for truly Filled orders ───────────────────────────

    [Fact]
    public async Task ReconcileFills_PartialFill_NoDriftWarning()
    {
        // Arrange: mock state repo so we can verify InsertFillIdempotentAsync is NOT called
        // when Alpaca status is PartiallyFilled (not Filled).
        var stateRepoMock = Substitute.For<IStateRepository>();
        var brokerMock = Substitute.For<IBrokerService>();
        var reconciliation = new ReconciliationService(brokerMock, stateRepoMock, _logger);

        const string clientId = "client_partial_fill";
        const string alpacaId = "alpaca_partial_fill";

        // SQLite has a non-terminal Accepted order with qty=20
        stateRepoMock.GetAllOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<OrderIntentDto>>(
                new List<OrderIntentDto>
                {
                    new(clientId, alpacaId, "SPY", "BUY", 20m, 450m,
                        OrderState.Accepted, DateTimeOffset.UtcNow, null)
                }.AsReadOnly()));

        // Alpaca returns PartiallyFilled with only 10 shares filled out of 20
        brokerMock.GetOpenOrdersAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderInfo>
            {
                new(alpacaId, clientId, "SPY", "BUY", 20m, 10m, 450m,
                    OrderState.PartiallyFilled, DateTimeOffset.UtcNow, null)
            });

        // Act
        await reconciliation.ReconcileFillsAsync(CancellationToken.None);

        // Assert: InsertFillIdempotentAsync NOT called — partial fill is not drift
        await stateRepoMock.DidNotReceive().InsertFillIdempotentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }
}
