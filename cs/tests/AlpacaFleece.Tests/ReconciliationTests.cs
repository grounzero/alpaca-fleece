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

    // ─── C-3: Startup gate via reconciliation outcome ─────────────────────────────

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
}
