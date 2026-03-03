namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for CorrelationService: pairwise correlation blocks, sector concentration blocks,
/// asset class concentration blocks, and pass-through scenarios.
/// </summary>
public sealed class CorrelationServiceTests
{
    private readonly IPositionTracker _positionTracker = Substitute.For<IPositionTracker>();
    private readonly ILogger<CorrelationService> _logger = Substitute.For<ILogger<CorrelationService>>();

    private static TradingOptions DefaultOptions(
        bool enabled = true,
        decimal maxCorrelation = 0.70m,
        decimal maxSectorPct = 0.20m,
        decimal maxAssetClassPct = 0.40m,
        int maxConcurrentPositions = 5,
        Dictionary<string, decimal>? staticCorrelations = null) => new()
    {
        RiskLimits = new RiskLimits { MaxConcurrentPositions = maxConcurrentPositions },
        CorrelationLimits = new CorrelationLimitsOptions
        {
            Enabled = enabled,
            MaxCorrelation = maxCorrelation,
            MaxSectorPct = maxSectorPct,
            MaxAssetClassPct = maxAssetClassPct,
            StaticCorrelations = staticCorrelations
                ?? new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase),
        },
    };

    private void SetupPositions(params string[] symbols)
    {
        var positions = symbols.ToDictionary(
            s => s,
            s => new PositionData(s, 10, 100m, 1m, 95m),
            StringComparer.OrdinalIgnoreCase);
        _positionTracker.GetAllPositions().Returns(positions);
    }

    private CorrelationService CreateService(TradingOptions? options = null) =>
        new(options ?? DefaultOptions(), _positionTracker, _logger);

    // ─── Disabled / empty ────────────────────────────────────────────────────────

    [Fact]
    public void Check_WhenDisabled_AlwaysPasses()
    {
        SetupPositions("TLT");
        var svc = CreateService(DefaultOptions(enabled: false, staticCorrelations: new()
        {
            ["TLT:IEF"] = 0.99m,  // would block if enabled
        }));

        var result = svc.Check("IEF");

        Assert.True(result.AllowsSignal);
        Assert.Equal("FILTER", result.RiskTier);
    }

    [Fact]
    public void Check_WhenNoExistingPositions_Passes()
    {
        SetupPositions(); // no positions
        var svc = CreateService();

        var result = svc.Check("AAPL");

        Assert.True(result.AllowsSignal);
    }

    [Fact]
    public void Check_WhenReversalSameSymbol_Passes()
    {
        // TLT:IEF has 0.99 correlation — would block any other symbol
        SetupPositions("TLT");
        var svc = CreateService(DefaultOptions(staticCorrelations: new()
        {
            ["TLT:IEF"] = 0.99m,
        }));

        // Signal is TLT itself (reversal) — should not block against itself
        var result = svc.Check("TLT");

        Assert.True(result.AllowsSignal);
    }

    // ─── Pairwise correlation ─────────────────────────────────────────────────────

    [Fact]
    public void Check_CorrelationExceedsLimit_Rejects()
    {
        // Scenario 1 from spec: TLT already held, IEF signal → correlation 0.85 > 0.70
        SetupPositions("TLT");
        var svc = CreateService(DefaultOptions(
            maxCorrelation: 0.70m,
            staticCorrelations: new() { ["TLT:IEF"] = 0.85m }));

        var result = svc.Check("IEF");

        Assert.False(result.AllowsSignal);
        Assert.Contains("0.85", result.Reason);
        Assert.Contains("0.70", result.Reason);
        Assert.Equal("FILTER", result.RiskTier);
    }

    [Fact]
    public void Check_CorrelationBelowLimit_Passes()
    {
        SetupPositions("TLT");
        var svc = CreateService(DefaultOptions(
            maxCorrelation: 0.90m,   // raised limit — 0.85 is now below threshold
            maxSectorPct: 1.0m,      // disable sector check to isolate correlation check
            maxAssetClassPct: 1.0m,  // disable asset class check
            staticCorrelations: new() { ["TLT:IEF"] = 0.85m }));

        var result = svc.Check("IEF");

        Assert.True(result.AllowsSignal);
    }

    [Fact]
    public void Check_CorrelationKeyReversedInConfig_StillDetected()
    {
        // Config has "IEF:TLT" instead of "TLT:IEF" — lookup should try both orderings
        SetupPositions("TLT");
        var svc = CreateService(DefaultOptions(
            maxCorrelation: 0.70m,
            staticCorrelations: new() { ["IEF:TLT"] = 0.85m }));  // reversed key

        var result = svc.Check("IEF");

        Assert.False(result.AllowsSignal);
        Assert.Contains("0.85", result.Reason);
    }

    [Fact]
    public void Check_UnknownSymbolPair_AssumedUncorrelated_Passes()
    {
        SetupPositions("AAPL");
        var svc = CreateService(DefaultOptions(
            maxCorrelation: 0.70m,
            maxSectorPct: 1.0m,   // disable sector check
            maxAssetClassPct: 1.0m,
            staticCorrelations: new())); // no entries

        // MSFT vs AAPL — not in config, assumed 0 correlation. Sector checks are
        // effectively disabled by maxSectorPct=1.0 to isolate correlation-only behavior.
        var result = svc.Check("MSFT");

        Assert.True(result.AllowsSignal);
    }

    // ─── Sector concentration ─────────────────────────────────────────────────────

    [Fact]
    public void Check_SectorConcentrationWouldExceedLimit_Rejects()
    {
        // Scenario 2 from spec: AAPL + MSFT already in Technology (2/5 = 40%)
        // Adding NVDA would make it 3/5 = 60% > 20% → rejected
        SetupPositions("AAPL", "MSFT");
        var svc = CreateService(DefaultOptions(
            maxSectorPct: 0.20m,
            maxAssetClassPct: 1.0m,   // isolate sector check
            maxConcurrentPositions: 5,
            staticCorrelations: new())); // no pairwise correlations

        var result = svc.Check("NVDA");

        Assert.False(result.AllowsSignal);
        Assert.Contains("Technology", result.Reason);
        Assert.Equal("FILTER", result.RiskTier);
    }

    [Fact]
    public void Check_SectorCountAtLimit_Passes()
    {
        // 1 existing Technology position; adding another = 2/10 = 20% = exactly at limit → passes
        SetupPositions("AAPL");
        var svc = CreateService(DefaultOptions(
            maxSectorPct: 0.20m,
            maxAssetClassPct: 1.0m,
            maxConcurrentPositions: 10,
            staticCorrelations: new()));

        var result = svc.Check("MSFT");

        Assert.True(result.AllowsSignal);
    }

    [Fact]
    public void Check_UnknownSectorSymbol_SkipsSectorCheck_Passes()
    {
        SetupPositions("AAPL");
        // FAKESTOCK is not in SectorMapping — sector check is skipped for unknown sectors
        var svc = CreateService(DefaultOptions(
            maxSectorPct: 0.01m,    // very tight — would block if sector was known
            maxAssetClassPct: 1.0m,
            maxConcurrentPositions: 5,
            staticCorrelations: new()));

        var result = svc.Check("FAKESTOCK");

        Assert.True(result.AllowsSignal);
    }

    // ─── Asset class concentration ────────────────────────────────────────────────

    [Fact]
    public void Check_AssetClassConcentrationWouldExceedLimit_Rejects()
    {
        // Scenario 3: SPY + QQQ + AAPL + MSFT = 4 equities / 5 max = 80% > 40%
        SetupPositions("SPY", "QQQ", "AAPL", "MSFT");
        var svc = CreateService(DefaultOptions(
            maxSectorPct: 1.0m,        // isolate asset class check
            maxAssetClassPct: 0.40m,
            maxConcurrentPositions: 5,
            staticCorrelations: new())); // no pairwise entries

        var result = svc.Check("NVDA");

        Assert.False(result.AllowsSignal);
        Assert.Contains("Equity", result.Reason);
        Assert.Equal("FILTER", result.RiskTier);
    }

    [Fact]
    public void Check_AssetClassCountAtLimit_Passes()
    {
        // 1 bond (TLT); adding IEF = 2/5 = 40% = exactly at limit → passes
        SetupPositions("TLT");
        var svc = CreateService(DefaultOptions(
            maxSectorPct: 1.0m,
            maxAssetClassPct: 0.40m,
            maxConcurrentPositions: 5,
            staticCorrelations: new()));

        var result = svc.Check("IEF");

        Assert.True(result.AllowsSignal);
    }

    [Fact]
    public void Check_MixedAssetClasses_Passes()
    {
        // 1 equity (AAPL) + adding a bond (TLT) — different asset class, no correlation
        SetupPositions("AAPL");
        var svc = CreateService(DefaultOptions(
            maxSectorPct: 1.0m,
            maxAssetClassPct: 0.40m,
            maxConcurrentPositions: 5,
            staticCorrelations: new()));

        var result = svc.Check("TLT");

        Assert.True(result.AllowsSignal);
    }

    // ─── All checks pass ─────────────────────────────────────────────────────────

    [Fact]
    public void Check_DiversifiedPortfolio_Passes()
    {
        // AAPL (tech equity), TLT (bond), GLD (commodity) — all different, uncorrelated
        SetupPositions("AAPL", "TLT");
        var svc = CreateService(DefaultOptions(
            maxCorrelation: 0.70m,
            maxSectorPct: 0.20m,
            maxAssetClassPct: 0.40m,
            maxConcurrentPositions: 10,
            staticCorrelations: new()));

        var result = svc.Check("GLD");

        Assert.True(result.AllowsSignal);
        Assert.Equal("FILTER", result.RiskTier);
    }
}
