namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for regime-based strategy routing: RegimeRouter + StrategyOrchestrator in Regime mode.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class RegimeRoutingTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<StrategyOrchestrator> _orchestratorLogger =
        Substitute.For<ILogger<StrategyOrchestrator>>();
    private readonly ILogger<RegimeRouter> _routerLogger =
        Substitute.For<ILogger<RegimeRouter>>();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync()    => Task.CompletedTask;

    // ── helpers ────────────────────────────────────────────────────────────────

    private static BarEvent MakeBar(string symbol, decimal close, bool recent = true) => new(
        Symbol: symbol,
        Timeframe: "1Min",
        Timestamp: recent ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow.AddMinutes(-60),
        Open: close - 1m, High: close + 1m, Low: close - 1m, Close: close, Volume: 1_000_000);

    /// <summary>
    /// Builds a StrategyOrchestrator in Regime mode with two strategies (SMA + RSI)
    /// and the provided RegimeRouter.
    /// </summary>
    private StrategyOrchestrator BuildRegimeOrchestrator(
        IStrategy smaStrategy, IStrategyMetadata smaMeta,
        IStrategy rsiStrategy, IStrategyMetadata rsiMeta,
        RegimeRouter? router = null)
    {
        var registry = new StrategyRegistry();
        registry.Register(smaStrategy, smaMeta);
        registry.Register(rsiStrategy, rsiMeta);

        var options = new TradingOptions
        {
            StrategySelection = new StrategySelectionOptions
            {
                Mode = "Regime",
                RegimeMappings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["TRENDING_UP"]   = ["SMA_Strat"],
                    ["TRENDING_DOWN"] = ["SMA_Strat"],
                    ["RANGING"]       = ["RSI_Strat"],
                    ["DEFAULT"]       = ["SMA_Strat"],
                }
            }
        };

        return new StrategyOrchestrator(registry, options, _orchestratorLogger, router);
    }

    private static (IStrategy Strategy, IStrategyMetadata Metadata) MakeEntry(
        string name, Action? onBar = null)
    {
        var strategy = Substitute.For<IStrategy>();
        if (onBar is not null)
            strategy.OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>())
                .Returns(_ => { onBar(); return ValueTask.CompletedTask; });
        else
            strategy.OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.CompletedTask);

        var meta = Substitute.For<IStrategyMetadata>();
        meta.StrategyName.Returns(name);

        return (strategy, meta);
    }

    // ── RegimeRouter unit tests ────────────────────────────────────────────────

    [Fact]
    public void GetRegime_BeforeWarmup_ReturnsDefault()
    {
        var router = new RegimeRouter(_routerLogger);
        // No bars fed yet
        Assert.Equal("DEFAULT", router.GetRegime("AAPL"));
    }

    [Fact]
    public void GetRegime_DuringWarmup_ReturnsDefault()
    {
        var router = new RegimeRouter(_routerLogger);
        // Feed 20 bars (RequiredBars = 21, so still pre-warmup)
        for (var i = 0; i < 20; i++)
            router.Update(MakeBar("AAPL", 100m + i));

        Assert.Equal("DEFAULT", router.GetRegime("AAPL"));
    }

    [Fact]
    public void GetRegime_AfterWarmup_TrendingUp_ReturnsTrendingUp()
    {
        var router = new RegimeRouter(_routerLogger);
        // Feed 21 bars in a steady uptrend so fast SMA > medium SMA > slow SMA.
        // Prices rise monotonically so that fast (SMA-5) > medium (SMA-10) > slow (SMA-20).
        for (var i = 0; i < 21; i++)
            router.Update(MakeBar("AAPL", 100m + i * 2m));

        Assert.Equal("TRENDING_UP", router.GetRegime("AAPL"));
    }

    [Fact]
    public void GetRegime_AfterWarmup_TrendingDown_ReturnsTrendingDown()
    {
        var router = new RegimeRouter(_routerLogger);
        // Prices fall monotonically so that fast < medium < slow.
        for (var i = 0; i < 21; i++)
            router.Update(MakeBar("AAPL", 200m - i * 2m));

        Assert.Equal("TRENDING_DOWN", router.GetRegime("AAPL"));
    }

    [Fact]
    public void GetRegime_IsolatedPerSymbol()
    {
        var router = new RegimeRouter(_routerLogger);

        // AAPL: uptrend
        for (var i = 0; i < 21; i++)
            router.Update(MakeBar("AAPL", 100m + i * 2m));

        // MSFT: downtrend
        for (var i = 0; i < 21; i++)
            router.Update(MakeBar("MSFT", 200m - i * 2m));

        Assert.Equal("TRENDING_UP",   router.GetRegime("AAPL"));
        Assert.Equal("TRENDING_DOWN", router.GetRegime("MSFT"));
        Assert.Equal("DEFAULT",       router.GetRegime("GOOG")); // untouched symbol
    }

    // ── StrategyOrchestrator Regime mode integration tests ────────────────────

    [Fact]
    public async Task RegimeMode_PreWarmup_DispatchesToDefaultMapping()
    {
        // DEFAULT maps to SMA — verify only sma.OnBarAsync is called
        var smaCalled = 0;
        var rsiCalled = 0;

        var (smaStrat, smaMeta) = MakeEntry("SMA_Strat", () => smaCalled++);
        var (rsiStrat, rsiMeta) = MakeEntry("RSI_Strat", () => rsiCalled++);

        var router = new RegimeRouter(_routerLogger);
        // No bars → regime = DEFAULT → mapped to SMA_Strat
        var orchestrator = BuildRegimeOrchestrator(smaStrat, smaMeta, rsiStrat, rsiMeta, router);

        await orchestrator.DispatchBarAsync(MakeBar("AAPL", 150m));

        Assert.Equal(1, smaCalled);
        Assert.Equal(0, rsiCalled);
    }

    [Fact]
    public async Task RegimeMode_TrendingUp_DispatchesToSmaOnly()
    {
        var smaCalled = 0;
        var rsiCalled = 0;

        var (smaStrat, smaMeta) = MakeEntry("SMA_Strat", () => smaCalled++);
        var (rsiStrat, rsiMeta) = MakeEntry("RSI_Strat", () => rsiCalled++);

        var router = new RegimeRouter(_routerLogger);
        // Warm up the router with a strong uptrend
        for (var i = 0; i < 21; i++)
            router.Update(MakeBar("AAPL", 100m + i * 2m));

        Assert.Equal("TRENDING_UP", router.GetRegime("AAPL"));

        var orchestrator = BuildRegimeOrchestrator(smaStrat, smaMeta, rsiStrat, rsiMeta, router);
        await orchestrator.DispatchBarAsync(MakeBar("AAPL", 150m));

        Assert.Equal(1, smaCalled);
        Assert.Equal(0, rsiCalled);
    }

    [Fact]
    public async Task RegimeMode_TrendingDown_DispatchesToSmaOnly()
    {
        var smaCalled = 0;
        var rsiCalled = 0;

        var (smaStrat, smaMeta) = MakeEntry("SMA_Strat", () => smaCalled++);
        var (rsiStrat, rsiMeta) = MakeEntry("RSI_Strat", () => rsiCalled++);

        var router = new RegimeRouter(_routerLogger);
        for (var i = 0; i < 21; i++)
            router.Update(MakeBar("AAPL", 200m - i * 2m));

        Assert.Equal("TRENDING_DOWN", router.GetRegime("AAPL"));

        var orchestrator = BuildRegimeOrchestrator(smaStrat, smaMeta, rsiStrat, rsiMeta, router);
        await orchestrator.DispatchBarAsync(MakeBar("AAPL", 150m));

        Assert.Equal(1, smaCalled);
        Assert.Equal(0, rsiCalled);
    }

    [Fact]
    public async Task RegimeMode_WithoutRouter_FallsThrough_DispatchesAll()
    {
        // When no RegimeRouter is provided, the Regime mode condition (_isRegimeMode && regimeRouter is not null)
        // is false, so all strategies are dispatched as Multi mode.
        var smaCalled = 0;
        var rsiCalled = 0;

        var (smaStrat, smaMeta) = MakeEntry("SMA_Strat", () => smaCalled++);
        var (rsiStrat, rsiMeta) = MakeEntry("RSI_Strat", () => rsiCalled++);

        // No router passed (null)
        var orchestrator = BuildRegimeOrchestrator(smaStrat, smaMeta, rsiStrat, rsiMeta, router: null);
        await orchestrator.DispatchBarAsync(MakeBar("AAPL", 150m));

        Assert.Equal(1, smaCalled);
        Assert.Equal(1, rsiCalled);
    }

    [Fact]
    public async Task RegimeMode_UnmappedRegime_FallsBackToDefault()
    {
        // The orchestrator should fall back to the DEFAULT mapping when a regime has no specific entry.
        var smaCalled = 0;
        var rsiCalled = 0;

        var (smaStrat, smaMeta) = MakeEntry("SMA_Strat", () => smaCalled++);
        var (rsiStrat, rsiMeta) = MakeEntry("RSI_Strat", () => rsiCalled++);

        var registry = new StrategyRegistry();
        registry.Register(smaStrat, smaMeta);
        registry.Register(rsiStrat, rsiMeta);

        var options = new TradingOptions
        {
            StrategySelection = new StrategySelectionOptions
            {
                Mode = "Regime",
                RegimeMappings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    // "SIDEWAYS" is deliberately absent; only DEFAULT present
                    ["DEFAULT"] = ["SMA_Strat"],
                }
            }
        };

        // Router not warmed up → regime = "DEFAULT" → DEFAULT mapping → SMA_Strat only
        var router = new RegimeRouter(_routerLogger);
        var orchestrator = new StrategyOrchestrator(registry, options, _orchestratorLogger, router);

        await orchestrator.DispatchBarAsync(MakeBar("AAPL", 150m));

        Assert.Equal(1, smaCalled);
        Assert.Equal(0, rsiCalled);
    }

    [Fact]
    public async Task RegimeMode_EmptyMappings_LogsWarningAndSkips()
    {
        var smaCalled = 0;
        var rsiCalled = 0;

        var (smaStrat, smaMeta) = MakeEntry("SMA_Strat", () => smaCalled++);
        var (rsiStrat, rsiMeta) = MakeEntry("RSI_Strat", () => rsiCalled++);

        var registry = new StrategyRegistry();
        registry.Register(smaStrat, smaMeta);
        registry.Register(rsiStrat, rsiMeta);

        // Completely empty regime mappings — no DEFAULT either
        var options = new TradingOptions
        {
            StrategySelection = new StrategySelectionOptions
            {
                Mode = "Regime",
                RegimeMappings = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            }
        };

        var router = new RegimeRouter(_routerLogger);
        var orchestrator = new StrategyOrchestrator(registry, options, _orchestratorLogger, router);

        await orchestrator.DispatchBarAsync(MakeBar("AAPL", 150m));

        // Bar should be skipped — warning logged, no strategies called
        Assert.Equal(0, smaCalled);
        Assert.Equal(0, rsiCalled);
    }
}
