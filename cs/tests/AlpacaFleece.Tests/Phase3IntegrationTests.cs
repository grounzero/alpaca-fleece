namespace AlpacaFleece.Tests;

/// <summary>
/// End-to-end Phase 3 integration tests.
/// Verifies complete flow: Signal → RiskManager → OrderManager → Database.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class Phase3IntegrationTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly IMarketDataClient _marketDataMock = Substitute.For<IMarketDataClient>();
    private readonly ILogger<RiskManager> _riskManagerLogger = Substitute.For<ILogger<RiskManager>>();
    private readonly ILogger<OrderManager> _orderManagerLogger = Substitute.For<ILogger<OrderManager>>();
    private readonly ILogger<VolatilityRegimeDetector> _volLogger = Substitute.For<ILogger<VolatilityRegimeDetector>>();
    private readonly TradingOptions _options = new()
    {
        Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
        Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
    };

    public async Task InitializeAsync()
    {
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "0");
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");
        await fixture.StateRepository.SetStateAsync("trading_ready", "true");
        await fixture.StateRepository.SaveCircuitBreakerCountAsync(0);

        // Cancel any non-terminal order intents left by previous tests so Gate 6b
        // does not incorrectly block signals for the same symbol+side.
        var nonTerminal = new[]
        {
            OrderState.PendingNew.ToString(), OrderState.Accepted.ToString(),
            OrderState.PartiallyFilled.ToString(), OrderState.PendingCancel.ToString(),
            OrderState.PendingReplace.ToString(),
        };
        var stale = await fixture.DbContext.OrderIntents
            .Where(i => nonTerminal.Contains(i.Status))
            .ToListAsync();
        foreach (var intent in stale)
            intent.Status = OrderState.Canceled.ToString();
        if (stale.Count > 0)
            await fixture.DbContext.SaveChangesAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task VolatilityAdaptation_Integration_HighVsLowRegime_ChangesOrderSize()
    {
        var options = new TradingOptions
        {
            Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
            Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) },
            RiskLimits = new RiskLimits
            {
                MaxPositionSizePct = 0.05m,
                MaxRiskPerTradePct = 0.01m,
                StopLossPct = 0.02m,
                MinSignalConfidence = 0.2m
            },
            VolatilityRegime = new VolatilityRegimeOptions
            {
                Enabled = true,
                TransitionConfirmationBars = 1,
                LowMaxVolatility = 0.001m,
                NormalMaxVolatility = 0.003m,
                HighMaxVolatility = 0.020m,
                LowPositionMultiplier = 1.2m,
                HighPositionMultiplier = 0.5m
            }
        };

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow.AddHours(15), DateTimeOffset.UtcNow.AddHours(7), DateTimeOffset.UtcNow));
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo("test", 10000m, 0m, 100000m, 0m, true, false, DateTimeOffset.UtcNow));

        // Stable series => low volatility
        var lowBars = new List<Quote>();
        var lowTs = DateTimeOffset.UtcNow.AddMinutes(-31);
        decimal lowPx = 100m;
        for (var i = 0; i < 31; i++)
        {
            lowPx *= 1.00005m;
            lowBars.Add(new Quote("LOW", lowTs.AddMinutes(i), lowPx, lowPx, lowPx, lowPx, 1000));
        }

        // Alternating +/-1% => high volatility
        var highBars = new List<Quote>();
        var highTs = DateTimeOffset.UtcNow.AddMinutes(-31);
        decimal highPx = 100m;
        for (var i = 0; i < 31; i++)
        {
            highPx *= i % 2 == 0 ? 1.01m : 0.99m;
            highBars.Add(new Quote("HIGH", highTs.AddMinutes(i), highPx, highPx, highPx, highPx, 1000));
        }

        _marketDataMock.GetBarsAsync("LOW", "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(lowBars.AsReadOnly()));
        _marketDataMock.GetBarsAsync("HIGH", "1m", Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyList<Quote>>(highBars.AsReadOnly()));

        var volDetector = new VolatilityRegimeDetector(_marketDataMock, options, _volLogger);
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock, riskManager, fixture.StateRepository, fixture.EventBus, options, _orderManagerLogger,
            volatilityRegimeDetector: volDetector);

        decimal qtyLow = 0m;
        decimal qtyHigh = 0m;
        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<decimal>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var symbol = ci.ArgAt<string>(0);
                var qty = ci.ArgAt<decimal>(2);
                if (symbol == "LOW") qtyLow = qty;
                if (symbol == "HIGH") qtyHigh = qty;
                return new OrderInfo(
                    AlpacaOrderId: $"alpaca_{symbol}",
                    ClientOrderId: ci.ArgAt<string>(4),
                    Symbol: symbol,
                    Side: ci.ArgAt<string>(1),
                    Quantity: qty,
                    FilledQuantity: 0m,
                    AverageFilledPrice: 0m,
                    Status: OrderState.Accepted,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: null);
            });

        var lowSignal = new SignalEvent(
            "LOW", "BUY", "1m", DateTimeOffset.UtcNow,
            new SignalMetadata((5, 15), 10m, 9m, 8m, 1m, 0.9m, "TRENDING_UP", 0.8m, 150m, BarsInRegime: 15));
        var highSignal = new SignalEvent(
            "HIGH", "BUY", "1m", DateTimeOffset.UtcNow.AddSeconds(1),
            new SignalMetadata((5, 15), 10m, 9m, 8m, 1m, 0.9m, "TRENDING_UP", 0.8m, 150m, BarsInRegime: 15));

        _ = await orderManager.SubmitSignalAsync(lowSignal, 0m, 150m);
        _ = await orderManager.SubmitSignalAsync(highSignal, 0m, 150m);

        Assert.True(qtyLow > qtyHigh, $"Expected low-vol qty > high-vol qty, got {qtyLow} vs {qtyHigh}");
        Assert.True(qtyLow > 0m && qtyHigh > 0m);
    }

    [Fact]
    public async Task SignalFlow_BarArrives_SignalGenerated_RiskCheckPasses_OrderSubmitted()
    {
        // Setup broker mock to approve orders and market conditions
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
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
                AlpacaOrderId: "alpaca_123",
                ClientOrderId: "client_123",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 50,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        // Create managers
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, _options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            _options,
            _orderManagerLogger);

        // Create signal
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
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        // Execute flow
        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Verify database state
        var intent = await fixture.StateRepository.GetOrderIntentAsync(clientOrderId);
        Assert.NotNull(intent);
        Assert.Equal("AAPL", intent.Symbol);
        Assert.Equal("BUY", intent.Side);
        Assert.Equal(50, intent.Quantity);
        Assert.NotNull(intent.AlpacaOrderId);
    }

    [Fact]
    public async Task DuplicateSignal_SubmittedTwice_SecondCallReturnsSameId()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
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
                AlpacaOrderId: "alpaca_456",
                ClientOrderId: "client_456",
                Symbol: "AAPL",
                Side: "BUY",
                Quantity: 50,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.Accepted,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null));

        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, _options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            _options,
            _orderManagerLogger);

        var signal = new SignalEvent(
            Symbol: "AAPL",
            Side: "BUY",
            Timeframe: "1m",
            SignalTimestamp: DateTimeOffset.Parse("2024-02-21T14:30:00Z"),
            Metadata: new SignalMetadata(
                SmaPeriod: (5, 15),
                FastSma: 150m,
                MediumSma: 149m,
                SlowSma: 145m,
                Atr: 2m,
                Confidence: 0.8m,
                Regime: "TRENDING_UP",
                RegimeStrength: 0.7m,
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        // Submit first time
        var id1 = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Submit identical signal again
        var id2 = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Should return same ID (idempotent)
        Assert.Equal(id1, id2);

        // Broker should only be called once
        await _brokerMock.Received(1).SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CircuitBreakerTrip_FiveConsecutiveFailures_BlocksSubsequentSignals()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        // Broker throws to simulate failures
        _brokerMock.SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromException<OrderInfo>(new Exception("Broker connection failed")));

        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, _options, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            _options,
            _orderManagerLogger);

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
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        // Simulate 5 failures
        for (int i = 0; i < 5; i++)
        {
            try
            {
                await orderManager.SubmitSignalAsync(signal, 50, 150.5m);
            }
            catch (OrderManagerException)
            {
                // Expected
            }
        }

        // Check circuit breaker is tripped
        var count = await fixture.StateRepository.GetCircuitBreakerCountAsync();
        Assert.Equal(5, count);

        // Next signal should fail due to circuit breaker
        var ex = await Assert.ThrowsAsync<RiskManagerException>(
            async () => await orderManager.SubmitSignalAsync(signal, 50, 150.5m));

        Assert.Contains("Circuit breaker", ex.Message);
    }

    [Fact]
    public async Task DryRunMode_OrderIntentPersisted_BrokerNotCalled()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(15),
                NextClose: DateTimeOffset.UtcNow.AddHours(7),
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test",
                CashAvailable: 10000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        var dryRunOptions = new TradingOptions
        {
            Execution = new ExecutionOptions { DryRun = true },
            Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
            Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
        };
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, dryRunOptions, _riskManagerLogger);
        var orderManager = new OrderManager(
            _brokerMock,
            riskManager,
            fixture.StateRepository,
            fixture.EventBus,
            dryRunOptions,
            _orderManagerLogger);

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
                CurrentPrice: 150.5m,
                BarsInRegime: 15));

        var clientOrderId = await orderManager.SubmitSignalAsync(signal, 50, 150.5m);

        // Intent should be persisted
        var intent = await fixture.StateRepository.GetOrderIntentAsync(clientOrderId);
        Assert.NotNull(intent);

        // Broker should NOT be called
        await _brokerMock.DidNotReceive().SubmitOrderAsync(
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<decimal>(),
            Arg.Any<decimal>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OrderIdGenerator_ProducesDeterministicIds()
    {
        var strategy = "sma_crossover_multi";
        var symbol = "AAPL";
        var timeframe = "1Min";
        var timestamp = DateTimeOffset.Parse("2024-02-21T14:30:00Z");
        var side = "buy";

        var id1 = OrderIdGenerator.GenerateClientOrderId(strategy, symbol, timeframe, timestamp, side);
        var id2 = OrderIdGenerator.GenerateClientOrderId(strategy, symbol, timeframe, timestamp, side);

        // Same inputs should produce same ID
        Assert.Equal(id1, id2);

        // ID should be exactly 16 hex characters
        Assert.Equal(16, id1.Length);
        Assert.True(id1.All(c => "0123456789abcdef".Contains(c)));

        // Different side should produce different ID
        var idSell = OrderIdGenerator.GenerateClientOrderId(strategy, symbol, timeframe, timestamp, "sell");
        Assert.NotEqual(id1, idSell);
    }
}
