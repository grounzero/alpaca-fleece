namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for OrderManager (SHA-256 idempotency, persist-before-submit).
/// </summary>
[Collection("Trading Database Collection")]
public sealed class OrderManagerTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly ILogger<OrderManager> _logger = Substitute.For<ILogger<OrderManager>>();

    public async Task InitializeAsync()
    {
        // Cancel any non-terminal order intents left by previous tests so Gate 6b
        // does not incorrectly block signals for the same symbol+side.
        await CancelAllPendingIntentsAsync();
    }

    public async Task DisposeAsync() => await CancelAllPendingIntentsAsync();

    private async Task CancelAllPendingIntentsAsync()
    {
        var nonTerminal = new[]
        {
            OrderState.PendingNew.ToString(), OrderState.Accepted.ToString(),
            OrderState.PartiallyFilled.ToString(), OrderState.PendingCancel.ToString(),
            OrderState.PendingReplace.ToString(),
        };
        var intents = await fixture.DbContext.OrderIntents
            .Where(i => nonTerminal.Contains(i.Status))
            .ToListAsync();
        foreach (var intent in intents)
            intent.Status = OrderState.Canceled.ToString();
        if (intents.Count > 0)
            await fixture.DbContext.SaveChangesAsync();
    }

    [Fact]
    public async Task SubmitSignalAsync_GeneratesDeterministicClientOrderId()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: "", RiskTier: "FILTERS"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2024-02-21T10:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150.5m));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_123",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        // Same input should generate same ID
        var clientOrderId2 = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        Assert.NotEmpty(clientOrderId);
        Assert.Equal(16, clientOrderId.Length); // First 16 chars of SHA256
        Assert.Equal(clientOrderId, clientOrderId2);
    }

    [Fact]
    public async Task SubmitSignalAsync_PersistsBeforeSubmission()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: "", RiskTier: "FILTERS"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150.5m));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_456",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        // Check that intent was persisted
        var intent = await fixture.StateRepository.GetOrderIntentAsync(clientOrderId);
        Assert.NotNull(intent);
        Assert.Equal("AAPL", intent.Symbol);
        Assert.Equal("BUY", intent.Side);
    }

    [Fact]
    public async Task SubmitSignalAsync_SkipsResubmissionIfAlreadySubmitted()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: "", RiskTier: "FILTERS"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150.5m));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_789",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var clientOrderId1 = await orderManager.SubmitSignalAsync(signal, 100, 150m);
        var clientOrderId2 = await orderManager.SubmitSignalAsync(signal, 100, 150m);

        Assert.Equal(clientOrderId1, clientOrderId2);
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitSignalAsync_AutoSizesQuantity_WhenZeroPassedAsQuantity()
    {
        // qty = min(equity_cap, risk_cap)
        // equity_cap = floor(100000 * 0.05 / 150m) = floor(33.33) = 33
        // risk_cap   = floor(100000 * 0.01 / (150m * 0.02)) = floor(33.33) = 33
        // result = min(33, 33) = 33
        const decimal expectedQty = 33m;

        var options = new TradingOptions
        {
            RiskLimits = new RiskLimits { MaxPositionSizePct = 0.05m, MaxRiskPerTradePct = 0.01m, StopLossPct = 0.02m },
            Execution = new ExecutionOptions { DryRun = false },
        };

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: "", RiskTier: "FILTERS"));

        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2023-06-01T10:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m));

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 50000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_auto",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: expectedQty,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        // Act: pass quantity=0 (sentinel for auto-sizing via PositionSizer)
        await orderManager.SubmitSignalAsync(signal, 0, 150m);

        // Assert: broker received the auto-sized quantity
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Is<decimal>(q => q == expectedQty),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExitAsync_SubmitsExitOrder()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_exit",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "SELL",
                Quantity: 100,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        await orderManager.SubmitExitAsync("AAPL", "SELL", 100, 145m);

        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ── Issue 3: AllowFractionalOrders gate ───────────────────────────────────

    [Fact]
    public async Task AllowFractionalOrders_False_FloorsQuantity()
    {
        // Arrange: fractional orders disabled; qty 2.5 should floor to 2
        var options = new TradingOptions
        {
            Execution = new ExecutionOptions { DryRun = false, AllowFractionalOrders = false },
            Symbols = new SymbolLists { EquitySymbols = ["FRACTIONAL_FLOOR"] }
        };
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, "", "PASSED"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "FRACTIONAL_FLOOR",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2025-01-01T10:00:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 1m, Confidence: 0.8m, Regime: "TRENDING_UP",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo("alpaca_floor", "test", "FRACTIONAL_FLOOR", "BUY",
                2m, 0m, 0m, OrderState.PendingNew, DateTimeOffset.UtcNow, null));

        await orderManager.SubmitSignalAsync(signal, 2.5m, 100m);

        // Assert: broker received floored qty = 2 (not 2.5)
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<decimal>(q => q == 2m),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllowFractionalOrders_True_PreservesFractionalQuantity()
    {
        // Arrange: fractional orders enabled; qty 0.75 should pass through unchanged
        var options = new TradingOptions
        {
            Execution = new ExecutionOptions { DryRun = false, AllowFractionalOrders = true },
            Symbols = new SymbolLists { CryptoSymbols = ["BTC/USD_FRAC"] }
        };
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, "", "PASSED"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: "BTC/USD_FRAC",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2025-01-02T10:00:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 40000m, MediumSma: 39000m, SlowSma: 38000m,
                Atr: 500m, Confidence: 0.9m, Regime: "TRENDING_UP",
                RegimeStrength: 0.8m, CurrentPrice: 40000m, BarsInRegime: 20));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo("alpaca_frac", "test", "BTC/USD_FRAC", "BUY",
                0.75m, 0m, 0m, OrderState.PendingNew, DateTimeOffset.UtcNow, null));

        await orderManager.SubmitSignalAsync(signal, 0.75m, 40000m);

        // Assert: broker received the fractional qty unchanged
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Is<decimal>(q => q == 0.75m),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Issue 5: Pending-order gate ────────────────────────────────────────────

    [Fact]
    public async Task PendingOrderGate_BlocksSecondBuyWhenFirstPending()
    {
        // Arrange: save a non-terminal BUY intent for PGATE_A
        const string symbol = "PGATE_A";
        _ = await fixture.StateRepository.SaveOrderIntentAsync(
            "pgate_existing_buy", symbol, "BUY", 10m, 100m, DateTimeOffset.UtcNow);

        var options = new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = [symbol] }
        };
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, "", "PASSED"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: symbol, Side: "BUY", Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2025-03-01T11:00:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15), FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 1m, Confidence: 0.8m, Regime: "TRENDING_UP",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        // Act
        var result = await orderManager.SubmitSignalAsync(signal, 10m, 100m);

        // Assert: blocked by pending-order gate
        Assert.Empty(result);
        await _brokerMock.DidNotReceive().SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PendingOrderGate_AllowsSellWhenBuyPending()
    {
        // A pending BUY should NOT block a SELL signal (different side)
        const string symbol = "PGATE_B";
        _ = await fixture.StateRepository.SaveOrderIntentAsync(
            "pgate_buy_for_sell_test", symbol, "BUY", 10m, 100m, DateTimeOffset.UtcNow);
        await fixture.StateRepository.UpsertPositionTrackingAsync(symbol, 7m, 101m, 1m, 99m);

        var options = new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = [symbol] }
        };
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, "", "PASSED"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo("alpaca_sell", "test", symbol, "SELL",
                10m, 0m, 0m, OrderState.PendingNew, DateTimeOffset.UtcNow, null));

        var signal = new SignalEvent(
            Symbol: symbol, Side: "SELL", Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2025-03-01T11:01:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15), FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 1m, Confidence: 0.8m, Regime: "TRENDING_DOWN",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        var result = await orderManager.SubmitSignalAsync(signal, 10m, 100m);

        // SELL proceeds (not an ENTER action), and qty is clamped to open position size.
        Assert.NotEmpty(result);
        await _brokerMock.Received(1).SubmitOrderAsync(
            symbol, "SELL", 7m, 100m, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitSignalAsync_SellWithoutOpenPosition_Skips()
    {
        const string symbol = "SELL_NOPOS";
        var options = new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = [symbol] }
        };
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, "", "PASSED"));
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var signal = new SignalEvent(
            Symbol: symbol, Side: "SELL", Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2025-03-01T11:05:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15), FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 1m, Confidence: 0.8m, Regime: "TRENDING_DOWN",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        var result = await orderManager.SubmitSignalAsync(signal, 0m, 100m);

        Assert.Empty(result);
        await _brokerMock.DidNotReceive().SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitSignalAsync_SellWithOpenPosition_UsesTrackedQuantity()
    {
        const string symbol = "SELL_WITHPOS";
        await fixture.StateRepository.UpsertPositionTrackingAsync(symbol, 12m, 101m, 1m, 99m);

        var options = new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = [symbol] }
        };
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, "", "PASSED"));
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo("alpaca_sell_pos", "test", symbol, "SELL",
                12m, 0m, 0m, OrderState.PendingNew, DateTimeOffset.UtcNow, null));

        var signal = new SignalEvent(
            Symbol: symbol, Side: "SELL", Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2025-03-01T11:06:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15), FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 1m, Confidence: 0.8m, Regime: "TRENDING_DOWN",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        var result = await orderManager.SubmitSignalAsync(signal, 0m, 100m);

        Assert.NotEmpty(result);
        await _brokerMock.Received(1).SubmitOrderAsync(
            symbol, "SELL", 12m, 100m, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PendingOrderGate_AllowsAfterFirstFills()
    {
        // Arrange: create a pending intent then mark it as Filled (terminal)
        const string symbol = "PGATE_C";
        const string existingClientId = "pgate_filled_buy";
        _ = await fixture.StateRepository.SaveOrderIntentAsync(
            existingClientId, symbol, "BUY", 10m, 100m, DateTimeOffset.UtcNow);
        // Mark as Filled (terminal)
        await fixture.StateRepository.UpdateOrderIntentAsync(
            existingClientId, "alpaca_filled", OrderState.Filled, DateTimeOffset.UtcNow);

        var options = new TradingOptions
        {
            Symbols = new SymbolLists { EquitySymbols = [symbol] }
        };
        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, "", "PASSED"));
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo("alpaca_new", "test", symbol, "BUY",
                10m, 0m, 0m, OrderState.PendingNew, DateTimeOffset.UtcNow, null));

        var signal = new SignalEvent(
            Symbol: symbol, Side: "BUY", Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2025-03-01T12:00:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15), FastSma: 100m, MediumSma: 99m, SlowSma: 95m,
                Atr: 1m, Confidence: 0.8m, Regime: "TRENDING_UP",
                RegimeStrength: 0.7m, CurrentPrice: 100m, BarsInRegime: 15));

        var result = await orderManager.SubmitSignalAsync(signal, 10m, 100m);

        // Should be allowed (no pending order; only Filled = terminal)
        Assert.NotEmpty(result);
    }

    // ── Issue 6: Flatten persist-before-submit ────────────────────────────────

    [Fact]
    public async Task FlattenPositionsAsync_PersistsIntentBeforeBrokerCall()
    {
        // Arrange: broker throws after SaveOrderIntentAsync is called
        // The intent should be persisted even though broker fails
        var options = new TradingOptions { Execution = new ExecutionOptions { DryRun = false } };
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>
            {
                new("FLATTEN_A", 100m, 100m, 105m, 5m, 0.05m, DateTimeOffset.UtcNow)
            }.AsReadOnly() as IReadOnlyList<PositionInfo>);

        // Broker throws on SubmitOrderAsync
        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromException<OrderInfo>(new Exception("Broker unavailable")));

        // Act
        await orderManager.FlattenPositionsAsync();

        // Assert: intent was persisted to DB before broker was attempted
        var intents = await fixture.StateRepository.GetAllOrderIntentsAsync();
        var intent = intents.FirstOrDefault(i => i.Symbol == "FLATTEN_A" && i.Side == "SELL");
        Assert.NotNull(intent);
        Assert.Equal(OrderState.PendingNew, intent.Status);
        // AlpacaOrderId is null (broker never responded)
        Assert.Null(intent.AlpacaOrderId);
    }

    [Fact]
    public async Task FlattenPositionsAsync_IsIdempotent_AfterCrash()
    {
        // Arrange: intent already saved AND has AlpacaOrderId (already submitted once)
        // Second flatten call should NOT re-submit to broker
        var options = new TradingOptions { Execution = new ExecutionOptions { DryRun = false } };
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(_brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>
            {
                new("FLATTEN_B", 50m, 100m, 102m, 2m, 0.02m, DateTimeOffset.UtcNow)
            }.AsReadOnly() as IReadOnlyList<PositionInfo>);

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo("alpaca_flat_b", "test", "FLATTEN_B", "SELL",
                50m, 0m, 0m, OrderState.PendingNew, DateTimeOffset.UtcNow, null));

        // First flatten call → intent saved + submitted
        await orderManager.FlattenPositionsAsync();

        // Second flatten call (simulates crash + restart)
        await orderManager.FlattenPositionsAsync();

        // Assert: broker only called once (idempotency: AlpacaOrderId already set)
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FlattenPositions_SubmitsMarketOrders()
    {
        // Arrange
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>
            {
                new("MSFT", 50m, 300m, 310m, 500m, 0.016m, DateTimeOffset.UtcNow)
            }.AsReadOnly() as IReadOnlyList<PositionInfo>);

        decimal capturedLimitPrice = -1m;
        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedLimitPrice = callInfo.ArgAt<decimal>(3);
                return new ValueTask<OrderInfo>(new OrderInfo(
                    "alpaca_market_flat", "test", "MSFT", "SELL", 50m, 0m, 0m,
                    OrderState.PendingNew, DateTimeOffset.UtcNow, null));
            });

        // Act
        var submitted = await orderManager.FlattenPositionsAsync();

        // Assert: one order submitted with limitPrice=0m (market order)
        Assert.Equal(1, submitted);
        Assert.Equal(0m, capturedLimitPrice);
        await _brokerMock.Received(1).SubmitOrderAsync(
            "MSFT", "SELL", 50m, 0m, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExit_AlreadySubmitted_SkipsBroker()
    {
        // Arrange: save an intent with an AlpacaOrderId already set (simulates broker already received it)
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        // Derive the same clientOrderId that SubmitExitAsync would generate for today
        var nowUtc = DateTimeOffset.UtcNow;
        var dateKey = nowUtc.ToString("yyyyMMdd");
        var dayTs = new DateTimeOffset(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, TimeSpan.Zero);
        var clientOrderId = OrderIdGenerator.GenerateClientOrderId(
            strategy: "exit",
            symbol: "TSLA",
            timeframe: dateKey,
            signalTimestamp: dayTs,
            side: "sell");

        // Persist intent with AlpacaOrderId already set (as if broker already received it)
        _ = await fixture.StateRepository.SaveOrderIntentAsync(
            clientOrderId, "TSLA", "SELL", 25m, 0m, nowUtc);
        await fixture.StateRepository.UpdateOrderIntentAsync(
            clientOrderId, "alpaca_already_submitted", OrderState.Accepted, nowUtc);

        // Act: call SubmitExitAsync — should detect existing AlpacaOrderId and skip broker
        await orderManager.SubmitExitAsync("TSLA", "SELL", 25m, 0m);

        // Assert: broker.SubmitOrderAsync never called (idempotency guard)
        await _brokerMock.DidNotReceive().SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExitAsync_ActiveIntentSymbolMatch_IsCaseInsensitive()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        _ = await fixture.StateRepository.SaveOrderIntentAsync(
            "active_case_id",
            "AAPL",
            "SELL",
            10m,
            0m,
            DateTimeOffset.UtcNow);
        await fixture.StateRepository.UpdateOrderIntentAsync(
            "active_case_id",
            "alpaca_active_case",
            OrderState.Accepted,
            DateTimeOffset.UtcNow);

        await orderManager.SubmitExitAsync("aapl", "SELL", 10m, 0m);

        await _brokerMock.DidNotReceive().SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExitAsync_ReopenSameDay_SubmitsNewExitAfterPriorTerminalExit()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var submittedClientIds = new List<string>();
        _brokerMock.SubmitOrderAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var clientId = callInfo.ArgAt<string>(4);
                submittedClientIds.Add(clientId);
                return new ValueTask<OrderInfo>(new OrderInfo(
                    AlpacaOrderId: $"alpaca_{submittedClientIds.Count}",
                    ClientOrderId: clientId,
                    Symbol: "AAPL",
                    Side: "SELL",
                    Quantity: 10m,
                    FilledQuantity: 0m,
                    AverageFilledPrice: 0m,
                    Status: OrderState.Accepted,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: null));
            });

        await orderManager.SubmitExitAsync("AAPL", "SELL", 10m, 0m);
        Assert.Single(submittedClientIds);

        // Mark first exit terminal (e.g., position closed), then simulate a same-day re-entry and new exit.
        await fixture.StateRepository.UpdateOrderIntentAsync(
            submittedClientIds[0],
            "alpaca_1",
            OrderState.Filled,
            DateTimeOffset.UtcNow);

        await orderManager.SubmitExitAsync("AAPL", "SELL", 10m, 0m);

        Assert.Equal(2, submittedClientIds.Count);
        Assert.NotEqual(submittedClientIds[0], submittedClientIds[1]);
        await _brokerMock.Received(2).SubmitOrderAsync(
            "AAPL", "SELL", 10m, 0m, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExitAsync_RecoversPersistedUnsubmittedIntent()
    {
        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger);

        var recoveredClientOrderId = "recovered_exit_id";
        _ = await fixture.StateRepository.SaveOrderIntentAsync(
            recoveredClientOrderId, "NVDA", "SELL", 5m, 0m, DateTimeOffset.UtcNow);

        string? submittedClientOrderId = null;
        _brokerMock.SubmitOrderAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                submittedClientOrderId = callInfo.ArgAt<string>(4);
                return new ValueTask<OrderInfo>(new OrderInfo(
                    AlpacaOrderId: "alpaca_recovered",
                    ClientOrderId: submittedClientOrderId,
                    Symbol: "NVDA",
                    Side: "SELL",
                    Quantity: 5m,
                    FilledQuantity: 0m,
                    AverageFilledPrice: 0m,
                    Status: OrderState.Accepted,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: null));
            });

        await orderManager.SubmitExitAsync("NVDA", "SELL", 5m, 0m);

        Assert.Equal(recoveredClientOrderId, submittedClientOrderId);
        await _brokerMock.Received(1).SubmitOrderAsync(
            "NVDA", "SELL", 5m, 0m, recoveredClientOrderId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExitAsync_ConcurrentCallers_OnlyOneReachesBroker()
    {
        // Test that concurrent calls to SubmitExitAsync for the same symbol
        // result in only one broker submission.
        // Arrange
        var symbol = "EURUSD";
        var side = "SELL";
        var quantity = 15m;
        var limitPrice = 0m;

        var options = new TradingOptions();
        var riskManager = Substitute.For<IRiskManager>();

        // Setup broker mock to accept order
        _brokerMock.SubmitOrderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: $"alpaca_order_{Guid.NewGuid():N}"[..20],
                ClientOrderId: "test_exit",
                Symbol: symbol,
                Side: side,
                Quantity: quantity,
                FilledQuantity: 0m,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow
            ));

        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus,
            options, _logger);

        // Act: call SubmitExitAsync twice concurrently for the same symbol
        var task1 = orderManager.SubmitExitAsync(symbol, side, quantity, limitPrice);
        var task2 = orderManager.SubmitExitAsync(symbol, side, quantity, limitPrice);

        // Wait for both to complete
        await task1;
        await task2;

        // Assert: broker.SubmitOrderAsync called only once despite two concurrent calls
        var _ = _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Is<string>(s => s == symbol),
            Arg.Is<string>(s => s == side),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitExitAsync_SaveConflictWithTerminalIntent_RetriesWithNewId()
    {
        var stateRepository = Substitute.For<IStateRepository>();
        var broker = Substitute.For<IBrokerService>();
        var riskManager = Substitute.For<IRiskManager>();
        var eventBus = Substitute.For<IEventBus>();
        var options = new TradingOptions();

        stateRepository.GetNonTerminalOrderIntentsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<OrderIntentDto>().AsReadOnly());
        stateRepository.SaveOrderIntentAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<decimal>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<decimal?>())
            .Returns(new ValueTask<bool>(false), new ValueTask<bool>(true));
        stateRepository.GetOrderIntentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => new OrderIntentDto(
                ClientOrderId: call.ArgAt<string>(0),
                AlpacaOrderId: "terminal_conflict",
                Symbol: "AAPL",
                Side: "SELL",
                Quantity: 5m,
                LimitPrice: 0m,
                Status: OrderState.Filled,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: DateTimeOffset.UtcNow,
                AtrSeed: null));

        broker.SubmitOrderAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
                Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_new",
                ClientOrderId: "ignored",
                Symbol: "AAPL",
                Side: "SELL",
                Quantity: 5m,
                FilledQuantity: 0m,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var orderManager = new OrderManager(
            broker, riskManager, stateRepository, eventBus, options, _logger);

        await orderManager.SubmitExitAsync("AAPL", "SELL", 5m, 0m);

        await broker.Received(1).SubmitOrderAsync(
            "AAPL", "SELL", 5m, 0m, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await stateRepository.Received(2).SaveOrderIntentAsync(
            Arg.Any<string>(), "AAPL", "SELL", 5m, 0m,
            Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>(), Arg.Any<decimal?>());
    }

    [Fact]
    public async Task SubmitSignalAsync_AutoSizing_AppliesVolatilityMultiplier()
    {
        const string symbol = "VOLTEST1";
        // Base qty = 33, volatility high multiplier = 0.50 => floor(16.5) = 16
        const decimal expectedQty = 16m;

        var options = new TradingOptions
        {
            RiskLimits = new RiskLimits
            {
                MaxPositionSizePct = 0.05m,
                MaxRiskPerTradePct = 0.01m,
                StopLossPct = 0.02m
            },
            VolatilityRegime = new VolatilityRegimeOptions
            {
                Enabled = true,
                LookbackBars = 20,
                TransitionConfirmationBars = 1,
                LowMaxVolatility = 0.001m,
                NormalMaxVolatility = 0.003m,
                HighMaxVolatility = 0.020m,
                HighPositionMultiplier = 0.50m,
                HighStopMultiplier = 1.50m
            }
        };

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: "", RiskTier: "FILTERS"));

        var marketData = Substitute.For<IMarketDataClient>();
        var volLogger = Substitute.For<ILogger<VolatilityRegimeDetector>>();
        var volDetector = new VolatilityRegimeDetector(marketData, options, volLogger);

        var quotes = new List<Quote>();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-21);
        decimal px = 100m;
        for (var i = 0; i < 21; i++)
        {
            px *= i % 2 == 0 ? 1.01m : 0.99m; // ~1% alternating moves => high realised vol
            quotes.Add(new Quote(symbol, ts.AddMinutes(i), px, px, px, px, 1000));
        }
        marketData.GetBarsAsync(symbol, "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(quotes.AsReadOnly()));

        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger,
            volatilityRegimeDetector: volDetector);

        var signal = new SignalEvent(
            Symbol: symbol,
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2023-06-01T10:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m,
                BarsInRegime: 15));

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 50000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_auto_vol",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: expectedQty,
                FilledQuantity: 0m,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        _ = await orderManager.SubmitSignalAsync(signal, 0m, 150m);

        await _brokerMock.Received(1).SubmitOrderAsync(
            symbol, "BUY", expectedQty, 150m, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitSignalAsync_AutoSizing_StacksVolatilityAndDrawdownMultipliers()
    {
        const string symbol = "VOLTEST2";
        // Base qty = 33, volatility=0.5 => 16, drawdown warning=0.5 => 8
        const decimal expectedQty = 8m;

        var options = new TradingOptions
        {
            RiskLimits = new RiskLimits
            {
                MaxPositionSizePct = 0.05m,
                MaxRiskPerTradePct = 0.01m,
                StopLossPct = 0.02m
            },
            Drawdown = new DrawdownOptions { WarningPositionMultiplier = 0.50m },
            VolatilityRegime = new VolatilityRegimeOptions
            {
                Enabled = true,
                LookbackBars = 20,
                TransitionConfirmationBars = 1,
                LowMaxVolatility = 0.001m,
                NormalMaxVolatility = 0.003m,
                HighMaxVolatility = 0.020m,
                HighPositionMultiplier = 0.50m
            }
        };

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: "", RiskTier: "FILTERS"));

        var marketData = Substitute.For<IMarketDataClient>();
        var volDetector = new VolatilityRegimeDetector(
            marketData, options, Substitute.For<ILogger<VolatilityRegimeDetector>>());
        var quotes = new List<Quote>();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-21);
        decimal px = 100m;
        for (var i = 0; i < 21; i++)
        {
            px *= i % 2 == 0 ? 1.01m : 0.99m;
            quotes.Add(new Quote(symbol, ts.AddMinutes(i), px, px, px, px, 1000));
        }
        marketData.GetBarsAsync(symbol, "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(quotes.AsReadOnly()));

        var drawdownMonitor = new DrawdownMonitor(
            _brokerMock, fixture.StateRepository, options, Substitute.For<ILogger<DrawdownMonitor>>());
        var field = typeof(DrawdownMonitor).GetField("_currentLevel",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(drawdownMonitor, DrawdownLevel.Warning);

        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger,
            drawdownMonitor: drawdownMonitor,
            volatilityRegimeDetector: volDetector);

        var signal = new SignalEvent(
            Symbol: symbol,
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2023-06-01T10:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m,
                BarsInRegime: 15));

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 50000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_auto_stack",
                ClientOrderId: "test",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: expectedQty,
                FilledQuantity: 0m,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        _ = await orderManager.SubmitSignalAsync(signal, 0m, 150m);

        await _brokerMock.Received(1).SubmitOrderAsync(
            symbol, "BUY", expectedQty, 150m, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SubmitSignalAsync_AutoSizing_DoesNotExceedMaxPositionCapAfterVolatilityMultiplier()
    {
        const string symbol = "VOLCAP1";
        // Base qty = floor(100000 * 0.05 / 150) = 33
        // Low-vol multiplier = 1.2 => 39, but must be capped back to 33.
        const decimal expectedQty = 33m;

        var options = new TradingOptions
        {
            RiskLimits = new RiskLimits
            {
                MaxPositionSizePct = 0.05m,
                MaxRiskPerTradePct = 0.01m,
                StopLossPct = 0.02m
            },
            VolatilityRegime = new VolatilityRegimeOptions
            {
                Enabled = true,
                TransitionConfirmationBars = 1,
                LowMaxVolatility = 0.001m,
                NormalMaxVolatility = 0.003m,
                HighMaxVolatility = 0.020m,
                LowPositionMultiplier = 1.20m
            }
        };

        var riskManager = Substitute.For<IRiskManager>();
        riskManager.CheckSignalAsync(Arg.Any<SignalEvent>(), Arg.Any<CancellationToken>())
            .Returns(new RiskCheckResult(AllowsSignal: true, Reason: "", RiskTier: "FILTERS"));

        var marketData = Substitute.For<IMarketDataClient>();
        var volDetector = new VolatilityRegimeDetector(
            marketData, options, Substitute.For<ILogger<VolatilityRegimeDetector>>());

        var quotes = new List<Quote>();
        var ts = DateTimeOffset.UtcNow.AddMinutes(-21);
        decimal px = 100m;
        for (var i = 0; i < 21; i++)
        {
            px *= 1.00005m;
            quotes.Add(new Quote(symbol, ts.AddMinutes(i), px, px, px, px, 1000));
        }
        marketData.GetBarsAsync(symbol, "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(quotes.AsReadOnly()));

        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _logger,
            volatilityRegimeDetector: volDetector);

        var signal = new SignalEvent(
            Symbol: symbol,
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.UtcNow,
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150m,
                BarsInRegime: 15));

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 50000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(),
            Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new OrderInfo(
                AlpacaOrderId: "alpaca_auto_cap",
                ClientOrderId: "test",
                Symbol: symbol,
                Side: "BUY",
                Quantity: expectedQty,
                FilledQuantity: 0m,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        _ = await orderManager.SubmitSignalAsync(signal, 0m, 150m);

        await _brokerMock.Received(1).SubmitOrderAsync(
            symbol, "BUY", expectedQty, 150m, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
