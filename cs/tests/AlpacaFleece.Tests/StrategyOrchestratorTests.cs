namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for StrategyOrchestrator: dispatch modes, exception isolation, and slow-dispatch warnings.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class StrategyOrchestratorTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly ILogger<StrategyOrchestrator> _logger = Substitute.For<ILogger<StrategyOrchestrator>>();

    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => Task.CompletedTask;

    // ── helpers ────────────────────────────────────────────────────────────────

    private static BarEvent MakeBar(string symbol = "AAPL") => new(
        Symbol: symbol,
        Timeframe: "1Min",
        Timestamp: DateTimeOffset.UtcNow,
        Open: 150m, High: 152m, Low: 149m, Close: 151m, Volume: 1_000_000);

    private static (IStrategy Strategy, IStrategyMetadata Metadata) MakeEntry(
        string name, Func<BarEvent, CancellationToken, ValueTask>? impl = null)
    {
        var strategy = Substitute.For<IStrategy>();
        if (impl != null)
            strategy.OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>())
                .Returns(ci => impl(ci.Arg<BarEvent>(), ci.Arg<CancellationToken>()));
        else
            strategy.OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>())
                .Returns(ValueTask.CompletedTask);

        var metadata = Substitute.For<IStrategyMetadata>();
        metadata.StrategyName.Returns(name);
        return (strategy, metadata);
    }

    private StrategyOrchestrator BuildOrchestrator(
        IEnumerable<(IStrategy Strategy, IStrategyMetadata Metadata)> entries,
        string mode = "Single")
    {
        var registry = new StrategyRegistry();
        foreach (var e in entries)
            registry.Register(e.Strategy, e.Metadata);

        var options = new TradingOptions
        {
            StrategySelection = new StrategySelectionOptions { Mode = mode }
        };

        return new StrategyOrchestrator(registry, options, _logger);
    }

    // ── empty registry ─────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchBarAsync_LogsWarning_WhenNoStrategiesRegistered()
    {
        var orchestrator = BuildOrchestrator([]);
        var bar = MakeBar();

        // Should complete without throwing
        await orchestrator.DispatchBarAsync(bar);

        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }

    // ── single mode ────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchBarAsync_SingleMode_CallsStrategyOnce()
    {
        var entry = MakeEntry("Alpha");
        var orchestrator = BuildOrchestrator([entry], mode: "Single");
        var bar = MakeBar();

        await orchestrator.DispatchBarAsync(bar);

        await entry.Strategy.Received(1).OnBarAsync(
            Arg.Is<BarEvent>(b => b.Symbol == bar.Symbol),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBarAsync_SingleMode_PassesCancellationToken()
    {
        CancellationToken captured = default;
        var entry = MakeEntry("Alpha", (_, ct) =>
        {
            captured = ct;
            return ValueTask.CompletedTask;
        });

        var orchestrator = BuildOrchestrator([entry], mode: "Single");
        using var cts = new CancellationTokenSource();

        await orchestrator.DispatchBarAsync(MakeBar(), cts.Token);

        Assert.Equal(cts.Token, captured);
    }

    // ── multi mode ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchBarAsync_MultiMode_CallsAllStrategies()
    {
        var alpha = MakeEntry("Alpha");
        var beta  = MakeEntry("Beta");
        var orchestrator = BuildOrchestrator([alpha, beta], mode: "Multi");
        var bar = MakeBar();

        await orchestrator.DispatchBarAsync(bar);

        await alpha.Strategy.Received(1).OnBarAsync(Arg.Is<BarEvent>(b => b.Symbol == bar.Symbol), Arg.Any<CancellationToken>());
        await beta.Strategy.Received(1).OnBarAsync(Arg.Is<BarEvent>(b => b.Symbol == bar.Symbol), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBarAsync_RegimeMode_CallsAllStrategies()
    {
        // Regime is treated the same as Multi (any non-"Single" mode fans out)
        var alpha = MakeEntry("Alpha");
        var beta  = MakeEntry("Beta");
        var orchestrator = BuildOrchestrator([alpha, beta], mode: "Regime");
        var bar = MakeBar();

        await orchestrator.DispatchBarAsync(bar);

        await alpha.Strategy.Received(1).OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>());
        await beta.Strategy.Received(1).OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBarAsync_SingleEntry_UsesDirectPath_EvenInMultiMode()
    {
        // With only 1 strategy the orchestrator short-circuits to direct await regardless of mode
        var alpha = MakeEntry("Alpha");
        var orchestrator = BuildOrchestrator([alpha], mode: "Multi");

        await orchestrator.DispatchBarAsync(MakeBar());

        await alpha.Strategy.Received(1).OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>());
    }

    // ── exception isolation ────────────────────────────────────────────────────

    [Fact]
    public async Task DispatchBarAsync_MultiMode_IsolatesFailingStrategy()
    {
        // Alpha throws; Beta must still run and the orchestrator must not throw
        var alpha = MakeEntry("Alpha", (_, _) =>
            new ValueTask(Task.FromException(new InvalidOperationException("Alpha blew up"))));
        var beta = MakeEntry("Beta");

        var orchestrator = BuildOrchestrator([alpha, beta], mode: "Multi");

        await orchestrator.DispatchBarAsync(MakeBar()); // must not throw

        await beta.Strategy.Received(1).OnBarAsync(Arg.Any<BarEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBarAsync_MultiMode_LogsError_WhenStrategyFails()
    {
        var alpha = MakeEntry("Alpha", (_, _) =>
            new ValueTask(Task.FromException(new InvalidOperationException("boom"))));
        var orchestrator = BuildOrchestrator([alpha, MakeEntry("Beta")], mode: "Multi");

        await orchestrator.DispatchBarAsync(MakeBar());

        _logger.Received(1).Log(
            LogLevel.Error,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            Arg.Any<InvalidOperationException>(),
            Arg.Any<Func<object, Exception?, string>>());
    }

    [Fact]
    public async Task DispatchBarAsync_MultiMode_PropagatesCancellation()
    {
        var alpha = MakeEntry("Alpha", (_, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        });

        var orchestrator = BuildOrchestrator([alpha, MakeEntry("Beta")], mode: "Multi");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => orchestrator.DispatchBarAsync(MakeBar(), cts.Token).AsTask());
    }

    // ── slow dispatch warning ──────────────────────────────────────────────────

    [Fact]
    public async Task DispatchBarAsync_MultiMode_LogsWarning_WhenSlowDispatch()
    {
        // A strategy that sleeps longer than the 100ms threshold
        var slow = MakeEntry("Slow", async (_, ct) =>
        {
            await Task.Delay(200, ct);
        });

        var orchestrator = BuildOrchestrator([slow, MakeEntry("Fast")], mode: "Multi");

        await orchestrator.DispatchBarAsync(MakeBar());

        // Expect at least one Warning log (slow dispatch)
        _logger.Received(1).Log(
            LogLevel.Warning,
            Arg.Any<EventId>(),
            Arg.Any<object>(),
            null,
            Arg.Any<Func<object, Exception?, string>>());
    }
}
