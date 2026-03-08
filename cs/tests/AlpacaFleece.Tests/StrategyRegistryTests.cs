namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for StrategyRegistry and StrategyFactory (Phase 2: plugin infrastructure).
/// Uses a simple stub strategy so tests have no external dependencies.
/// </summary>
public sealed class StrategyRegistryTests
{
    // ── helpers ─────────────────────────────────────────────────────────────

    private static (IStrategy Strategy, IStrategyMetadata Metadata) MakeEntry(
        string name, string version = "1.0.0")
    {
        var stub = new StubStrategy(name, version);
        return (stub, stub);
    }

    private static ILogger<StrategyFactory> FactoryLogger() =>
        Substitute.For<ILogger<StrategyFactory>>();

    // ── StrategyRegistry ────────────────────────────────────────────────────

    [Fact]
    public void Registry_IsEmpty_WhenNothingRegistered()
    {
        var registry = new StrategyRegistry();
        Assert.True(registry.IsEmpty);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void Registry_Register_AddsStrategy()
    {
        var registry = new StrategyRegistry();
        var (strategy, metadata) = MakeEntry("Alpha");

        registry.Register(strategy, metadata);

        Assert.False(registry.IsEmpty);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Registry_Get_ReturnsStrategy_WhenRegistered()
    {
        var registry = new StrategyRegistry();
        var (strategy, metadata) = MakeEntry("Alpha");
        registry.Register(strategy, metadata);

        var found = registry.Get("Alpha");

        Assert.NotNull(found);
        Assert.Same(strategy, found);
    }

    [Fact]
    public void Registry_Get_ReturnsNull_WhenNotFound()
    {
        var registry = new StrategyRegistry();
        var (strategy, metadata) = MakeEntry("Alpha");
        registry.Register(strategy, metadata);

        Assert.Null(registry.Get("DoesNotExist"));
    }

    [Fact]
    public void Registry_Get_IsCaseInsensitive()
    {
        var registry = new StrategyRegistry();
        var (strategy, metadata) = MakeEntry("SMA_5x15_10x30_20x50");
        registry.Register(strategy, metadata);

        Assert.NotNull(registry.Get("sma_5x15_10x30_20x50"));
        Assert.NotNull(registry.Get("SMA_5X15_10X30_20X50"));
    }

    [Fact]
    public void Registry_Register_ThrowsOnDuplicateName()
    {
        var registry = new StrategyRegistry();
        var (s1, m1) = MakeEntry("Alpha");
        var (s2, m2) = MakeEntry("Alpha");
        registry.Register(s1, m1);

        Assert.Throws<InvalidOperationException>(() => registry.Register(s2, m2));
    }

    [Fact]
    public void Registry_GetAll_ReturnsAllEntries_InOrder()
    {
        var registry = new StrategyRegistry();
        var (s1, m1) = MakeEntry("Alpha");
        var (s2, m2) = MakeEntry("Beta");
        registry.Register(s1, m1);
        registry.Register(s2, m2);

        var all = registry.GetAll();

        Assert.Equal(2, all.Count);
        Assert.Equal("Alpha", all[0].Metadata.StrategyName);
        Assert.Equal("Beta", all[1].Metadata.StrategyName);
    }

    // ── StrategyFactory ─────────────────────────────────────────────────────

    [Fact]
    public void Factory_Build_RegistersActiveStrategies()
    {
        var factory = new StrategyFactory(FactoryLogger());
        var available = new[] { MakeEntry("Alpha"), MakeEntry("Beta") };
        var options = new StrategySelectionOptions
        {
            Mode = "Single",
            Active = ["Alpha"]
        };

        var registry = factory.Build(available, options);

        Assert.Equal(1, registry.Count);
        Assert.NotNull(registry.Get("Alpha"));
        Assert.Null(registry.Get("Beta")); // not in Active list
    }

    [Fact]
    public void Factory_Build_RegistersAllActive_InMultiMode()
    {
        var factory = new StrategyFactory(FactoryLogger());
        var available = new[] { MakeEntry("Alpha"), MakeEntry("Beta"), MakeEntry("Gamma") };
        var options = new StrategySelectionOptions
        {
            Mode = "Multi",
            Active = ["Alpha", "Gamma"]
        };

        var registry = factory.Build(available, options);

        Assert.Equal(2, registry.Count);
        Assert.NotNull(registry.Get("Alpha"));
        Assert.NotNull(registry.Get("Gamma"));
        Assert.Null(registry.Get("Beta"));
    }

    [Fact]
    public void Factory_Build_Throws_WhenActiveNameNotFound()
    {
        var factory = new StrategyFactory(FactoryLogger());
        var available = new[] { MakeEntry("Alpha") };
        var options = new StrategySelectionOptions
        {
            Mode = "Single",
            Active = ["DoesNotExist"]
        };

        Assert.Throws<InvalidOperationException>(() => factory.Build(available, options));
    }

    [Fact]
    public void Factory_Build_Throws_WhenActiveListIsEmpty()
    {
        var factory = new StrategyFactory(FactoryLogger());
        var available = new[] { MakeEntry("Alpha") };
        var options = new StrategySelectionOptions
        {
            Mode = "Single",
            Active = [] // nothing selected
        };

        Assert.Throws<InvalidOperationException>(() => factory.Build(available, options));
    }

    [Fact]
    public void SmaCrossoverStrategy_ImplementsIStrategyMetadata()
    {
        var strategy = new SmaCrossoverStrategy(
            Substitute.For<IEventBus>(),
            Substitute.For<ILogger<SmaCrossoverStrategy>>());

        // Verify both interfaces implemented
        Assert.IsAssignableFrom<IStrategy>(strategy);
        Assert.IsAssignableFrom<IStrategyMetadata>(strategy);
    }

    [Fact]
    public void SmaCrossoverStrategy_HasExpectedMetadata()
    {
        var strategy = new SmaCrossoverStrategy(
            Substitute.For<IEventBus>(),
            Substitute.For<ILogger<SmaCrossoverStrategy>>());

        Assert.Equal("SMA_5x15_10x30_20x50", strategy.StrategyName);
        Assert.False(string.IsNullOrEmpty(strategy.Version));
        Assert.False(string.IsNullOrEmpty(strategy.Description));
    }
}

// ── Stub ────────────────────────────────────────────────────────────────────

/// <summary>
/// Minimal strategy stub for registry and factory tests.
/// Implements both IStrategy and IStrategyMetadata so it can
/// be passed to StrategyFactory.Build without any external dependencies.
/// </summary>
file sealed class StubStrategy(string name, string version) : IStrategy, IStrategyMetadata
{
    public string StrategyName => name;
    public string Version => version;
    public string? Description => null;
    public int RequiredHistory => 1;
    public bool IsReady => true;
    public ValueTask OnBarAsync(BarEvent bar, CancellationToken ct = default) => ValueTask.CompletedTask;
}
