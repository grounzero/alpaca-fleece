namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for RsiMomentumStrategy: RSI calculation, crossover signal emission,
/// staleness gate, warmup behaviour, and IStrategyMetadata compliance.
/// </summary>
public sealed class RsiMomentumStrategyTests
{
    private readonly IEventBus _eventBus = Substitute.For<IEventBus>();
    private readonly ILogger<RsiMomentumStrategy> _logger = Substitute.For<ILogger<RsiMomentumStrategy>>();

    private RsiMomentumStrategy BuildStrategy(
        int period = 14,
        decimal oversold = 30m,
        int maxBarAgeMinutes = 0) // 0 = disabled for deterministic tests
    {
        var options = new TradingOptions
        {
            RsiMomentum = new RsiMomentumOptions
            {
                Period = period,
                OversoldThreshold = oversold,
                OverboughtThreshold = 70m
            }
        };
        var execution = new ExecutionOptions { MaxBarAgeMinutes = maxBarAgeMinutes };
        return new RsiMomentumStrategy(_eventBus, options, _logger, executionOptions: execution);
    }

    private static BarEvent MakeBar(string symbol, decimal close, decimal open = 100m,
        DateTimeOffset? timestamp = null)
        => new(
            Symbol: symbol,
            Timeframe: "1Min",
            Timestamp: timestamp ?? DateTimeOffset.UtcNow,
            Open: open,
            High: close + 0.5m,
            Low: close - 0.5m,
            Close: close,
            Volume: 500_000);

    // ── IStrategyMetadata ──────────────────────────────────────────────────

    [Fact]
    public void StrategyName_ReturnsExpectedName()
    {
        var strategy = BuildStrategy();
        Assert.Equal("RSI_Momentum_14", strategy.StrategyName);
    }

    [Fact]
    public void Version_IsSet()
    {
        var strategy = BuildStrategy();
        Assert.False(string.IsNullOrWhiteSpace(strategy.Version));
    }

    [Fact]
    public void Description_IsSet()
    {
        var strategy = BuildStrategy();
        Assert.False(string.IsNullOrWhiteSpace(strategy.Description));
    }

    // ── warmup ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task IsReady_FalseBeforeMinimumBars()
    {
        var strategy = BuildStrategy(period: 5);
        // RequiredBars = 5 + 2 = 7; feed only 6
        for (var i = 0; i < 6; i++)
            await strategy.OnBarAsync(MakeBar("AAPL", 100m + i));

        Assert.False(strategy.IsReady);
        await _eventBus.DidNotReceive().PublishAsync(Arg.Any<IEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task IsReady_TrueAfterMinimumBars()
    {
        var strategy = BuildStrategy(period: 5);
        // RequiredBars = 5 + 2 = 7; feed exactly 7 bars
        for (var i = 0; i < 7; i++)
            await strategy.OnBarAsync(MakeBar("AAPL", 100m + i));

        Assert.True(strategy.IsReady);
    }

    // ── RSI crossover signal ───────────────────────────────────────────────

    [Fact]
    public async Task OnBarAsync_EmitsBuySignal_WhenRsiCrossesIntoOversold()
    {
        // Use period=3 for a short warm-up (RequiredBars = period+2 = 5).
        // On the FIRST ready bar (bar 5), _previousRsi is seeded to the computed RSI but no
        // signal is emitted (prevRsi was 0, the default). The SECOND ready bar (bar 6) is
        // where the crossover is first detectable.
        var strategy = BuildStrategy(period: 3, oversold: 40m);

        // Bars 1-4: warmup (history.Count < RequiredBars → return early, prevRsi never set)
        await strategy.OnBarAsync(MakeBar("AAPL", 100m));
        await strategy.OnBarAsync(MakeBar("AAPL", 102m));
        await strategy.OnBarAsync(MakeBar("AAPL", 104m));
        await strategy.OnBarAsync(MakeBar("AAPL", 106m));
        // Bar 5: first ready bar — seeds _previousRsi ≈ 100 (all gains), no signal
        await strategy.OnBarAsync(MakeBar("AAPL", 108m));
        // Bar 6: sharp drop → prevRsi ≈ 100 ≥ 40, currentRsi ≈ 18 < 40 → CROSSOVER
        await strategy.OnBarAsync(MakeBar("AAPL", 90m));

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<SignalEvent>(s =>
                s.Symbol == "AAPL" &&
                s.Side == "BUY" &&
                s.StrategyName == "RSI_Momentum_14"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnBarAsync_NoSignal_WhenRsiAlreadyOversoldButNoNewCrossover()
    {
        // Two consecutive oversold bars: only the first crossover triggers.
        var strategy = BuildStrategy(period: 3, oversold: 40m);

        // Bars 1-4: warmup
        await strategy.OnBarAsync(MakeBar("AAPL", 100m));
        await strategy.OnBarAsync(MakeBar("AAPL", 102m));
        await strategy.OnBarAsync(MakeBar("AAPL", 104m));
        await strategy.OnBarAsync(MakeBar("AAPL", 106m));
        // Bar 5: baseline — seeds prevRsi ≈ 100, no signal
        await strategy.OnBarAsync(MakeBar("AAPL", 108m));
        // Bar 6: first drop — crossover fires (1 signal)
        await strategy.OnBarAsync(MakeBar("AAPL", 90m));
        // Bar 7: still oversold — prevRsi already < 40, no crossover (0 additional signals)
        await strategy.OnBarAsync(MakeBar("AAPL", 88m));

        // Should only have published once
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<SignalEvent>(s => s.Side == "BUY"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnBarAsync_NoSignal_WhenRsiAboveOversoldThreshold()
    {
        // All bars rising → RSI stays high, never oversold
        var strategy = BuildStrategy(period: 3, oversold: 30m);

        for (var i = 0; i < 10; i++)
            await strategy.OnBarAsync(MakeBar("AAPL", 100m + i * 2));

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Is<SignalEvent>(s => s.Side == "BUY"),
            Arg.Any<CancellationToken>());
    }

    // ── signal metadata ────────────────────────────────────────────────────

    [Fact]
    public async Task OnBarAsync_SignalMetadata_ContainsRsiValue()
    {
        var strategy = BuildStrategy(period: 3, oversold: 40m);

        await strategy.OnBarAsync(MakeBar("AAPL", 100m));
        await strategy.OnBarAsync(MakeBar("AAPL", 102m));
        await strategy.OnBarAsync(MakeBar("AAPL", 104m));
        await strategy.OnBarAsync(MakeBar("AAPL", 106m));
        await strategy.OnBarAsync(MakeBar("AAPL", 108m)); // baseline
        await strategy.OnBarAsync(MakeBar("AAPL", 90m));  // signal

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<SignalEvent>(s =>
                s.Metadata.FastSma > 0m &&   // carries RSI value
                s.Metadata.Regime == "OVERSOLD" &&
                s.Metadata.CurrentPrice == 90m),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnBarAsync_Confidence_BetweenZeroAndOne()
    {
        var strategy = BuildStrategy(period: 3, oversold: 40m);

        await strategy.OnBarAsync(MakeBar("AAPL", 100m));
        await strategy.OnBarAsync(MakeBar("AAPL", 102m));
        await strategy.OnBarAsync(MakeBar("AAPL", 104m));
        await strategy.OnBarAsync(MakeBar("AAPL", 106m));
        await strategy.OnBarAsync(MakeBar("AAPL", 108m)); // baseline
        await strategy.OnBarAsync(MakeBar("AAPL", 90m));  // signal

        await _eventBus.Received(1).PublishAsync(
            Arg.Is<SignalEvent>(s =>
                s.Metadata.Confidence >= 0.3m &&
                s.Metadata.Confidence <= 0.95m),
            Arg.Any<CancellationToken>());
    }

    // ── staleness gate ─────────────────────────────────────────────────────

    [Fact]
    public async Task OnBarAsync_SuppressesSignal_WhenBarIsStale()
    {
        var strategy = BuildStrategy(period: 3, oversold: 40m, maxBarAgeMinutes: 3);

        await strategy.OnBarAsync(MakeBar("AAPL", 100m));
        await strategy.OnBarAsync(MakeBar("AAPL", 102m));
        await strategy.OnBarAsync(MakeBar("AAPL", 104m));
        await strategy.OnBarAsync(MakeBar("AAPL", 106m));

        // Stale bar (5 minutes old) — should suppress signal
        var staleTs = DateTimeOffset.UtcNow.AddMinutes(-5);
        await strategy.OnBarAsync(MakeBar("AAPL", 90m, timestamp: staleTs));

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<IEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OnBarAsync_StaleBarUpdatesPrevRsi_SoFreshBarCrossoverIsAccurate()
    {
        // After a stale bar updates _previousRsi, the next fresh bar should detect crossover correctly
        var strategy = BuildStrategy(period: 3, oversold: 40m, maxBarAgeMinutes: 3);

        await strategy.OnBarAsync(MakeBar("AAPL", 100m));
        await strategy.OnBarAsync(MakeBar("AAPL", 102m));
        await strategy.OnBarAsync(MakeBar("AAPL", 104m));
        await strategy.OnBarAsync(MakeBar("AAPL", 106m)); // warm up: prevRsi above threshold

        // Stale bar drops into oversold — suppressed but prevRsi updated
        var staleTs = DateTimeOffset.UtcNow.AddMinutes(-5);
        await strategy.OnBarAsync(MakeBar("AAPL", 90m, timestamp: staleTs));

        // Fresh bar still oversold — prevRsi is now below threshold so NO crossover
        await strategy.OnBarAsync(MakeBar("AAPL", 88m)); // prevRsi already < 40, no crossover

        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Any<IEvent>(), Arg.Any<CancellationToken>());
    }

    // ── multi-symbol isolation ─────────────────────────────────────────────

    [Fact]
    public async Task OnBarAsync_TracksSymbolsIndependently()
    {
        var strategy = BuildStrategy(period: 3, oversold: 40m);

        // Warm up AAPL — needs warmup(4) + baseline(1) + signal(1) = 6 bars
        await strategy.OnBarAsync(MakeBar("AAPL", 100m));
        await strategy.OnBarAsync(MakeBar("AAPL", 102m));
        await strategy.OnBarAsync(MakeBar("AAPL", 104m));
        await strategy.OnBarAsync(MakeBar("AAPL", 106m));
        await strategy.OnBarAsync(MakeBar("AAPL", 108m)); // baseline
        await strategy.OnBarAsync(MakeBar("AAPL", 90m));  // fires for AAPL

        // MSFT only partially warmed up — no signal expected
        await strategy.OnBarAsync(MakeBar("MSFT", 200m));
        await strategy.OnBarAsync(MakeBar("MSFT", 202m));

        // Exactly one signal (AAPL)
        await _eventBus.Received(1).PublishAsync(
            Arg.Is<SignalEvent>(s => s.Symbol == "AAPL"),
            Arg.Any<CancellationToken>());
        await _eventBus.DidNotReceive().PublishAsync(
            Arg.Is<SignalEvent>(s => s.Symbol == "MSFT"),
            Arg.Any<CancellationToken>());
    }
}
