namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for DrawdownMonitor: level transitions, peak tracking, position multiplier,
/// and integration with RiskManager.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class DrawdownMonitorTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly ILogger<DrawdownMonitor> _monitorLogger = Substitute.For<ILogger<DrawdownMonitor>>();
    private readonly ILogger<RiskManager> _riskLogger = Substitute.For<ILogger<RiskManager>>();

    private static TradingOptions DefaultOptions(
        decimal warning = 0.03m,
        decimal halt = 0.05m,
        decimal emergency = 0.10m,
        decimal warningMultiplier = 0.5m,
        bool enabled = true) => new()
    {
        Drawdown = new DrawdownOptions
        {
            Enabled = enabled,
            WarningThresholdPct = warning,
            HaltThresholdPct = halt,
            EmergencyThresholdPct = emergency,
            WarningPositionMultiplier = warningMultiplier,
            CheckIntervalSeconds = 60
        },
        Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
        Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
    };

    public async Task InitializeAsync()
    {
        // Reset drawdown state before each test
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 0m, 0m, DateTimeOffset.UtcNow, false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Level detection ────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ReturnsNormal_WhenNoDrawdown()
    {
        SetupEquity(100_000m);
        var monitor = CreateMonitor(DefaultOptions());

        var (previous, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Normal, current);
        Assert.Equal(0m, pct);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsWarning_AtThreshold()
    {
        // Persist peak = 100k, now equity = 97k → drawdown = 3% = warning threshold
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(97_000m);
        var monitor = CreateMonitor(DefaultOptions());

        var (_, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Warning, current);
        Assert.Equal(0.03m, pct);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsHalt_AtThreshold()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(95_000m); // 5% drawdown
        var monitor = CreateMonitor(DefaultOptions());

        var (_, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Halt, current);
        Assert.Equal(0.05m, pct);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsEmergency_AtThreshold()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(90_000m); // 10% drawdown
        var monitor = CreateMonitor(DefaultOptions());

        var (_, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Emergency, current);
        Assert.Equal(0.10m, pct);
    }

    [Fact]
    public async Task UpdateAsync_ReturnsEmergency_AboveThreshold()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(85_000m); // 15% drawdown
        var monitor = CreateMonitor(DefaultOptions());

        var (_, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Emergency, current);
        Assert.Equal(0.15m, pct);
    }

    // ─── Peak tracking ──────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_UpdatesPeakWhenEquityRises()
    {
        // Peak was 100k; equity is now 110k → new peak should be 110k (no drawdown)
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(110_000m);
        var monitor = CreateMonitor(DefaultOptions());

        var (_, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Normal, current);
        Assert.Equal(0m, pct);

        // Persisted peak should be 110k
        var state = await fixture.StateRepository.GetDrawdownStateAsync();
        Assert.NotNull(state);
        Assert.Equal(110_000m, state.PeakEquity);
    }

    [Fact]
    public async Task UpdateAsync_InitialisesPeakFromCurrentEquityWhenNoPriorState()
    {
        // No prior state: peak is set to current equity, drawdown = 0
        SetupEquity(50_000m);
        var monitor = CreateMonitor(DefaultOptions());

        var (_, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Normal, current);
        Assert.Equal(0m, pct);

        var state = await fixture.StateRepository.GetDrawdownStateAsync();
        Assert.NotNull(state);
        Assert.Equal(50_000m, state.PeakEquity);
    }

    // ─── State persistence ──────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_PersistsLevelToDatabase()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(94_000m); // 6% → Halt
        var monitor = CreateMonitor(DefaultOptions());

        await monitor.UpdateAsync();

        var state = await fixture.StateRepository.GetDrawdownStateAsync();
        Assert.NotNull(state);
        Assert.Equal(DrawdownLevel.Halt, state.Level);
        Assert.Equal(0.06m, state.CurrentDrawdownPct);
    }

    // ─── GetCurrentLevel (in-memory cache) ──────────────────────────────────────

    [Fact]
    public async Task GetCurrentLevel_ReflectsLastUpdate()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(95_000m); // 5% → Halt
        var monitor = CreateMonitor(DefaultOptions());

        Assert.Equal(DrawdownLevel.Normal, monitor.GetCurrentLevel()); // before update

        await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Halt, monitor.GetCurrentLevel()); // after update
    }

    // ─── Position multiplier ────────────────────────────────────────────────────

    [Fact]
    public async Task GetPositionMultiplier_ReturnsHalf_InWarningState()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(97_000m); // 3% → Warning
        var monitor = CreateMonitor(DefaultOptions(warningMultiplier: 0.5m));

        await monitor.UpdateAsync();

        Assert.Equal(0.5m, monitor.GetPositionMultiplier());
    }

    [Fact]
    public async Task GetPositionMultiplier_ReturnsOne_InNormalState()
    {
        SetupEquity(100_000m);
        var monitor = CreateMonitor(DefaultOptions());

        await monitor.UpdateAsync();

        Assert.Equal(1.0m, monitor.GetPositionMultiplier());
    }

    [Fact]
    public async Task GetPositionMultiplier_ReturnsOne_InHaltState()
    {
        // In Halt, multiplier is irrelevant (no new orders), but should still return 1.0
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(95_000m);
        var monitor = CreateMonitor(DefaultOptions());

        await monitor.UpdateAsync();

        Assert.Equal(1.0m, monitor.GetPositionMultiplier());
    }

    // ─── Disabled ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_DoesNothing_WhenDisabled()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(80_000m); // would be Emergency if enabled
        var monitor = CreateMonitor(DefaultOptions(enabled: false));

        var (previous, current, pct) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Normal, previous);
        Assert.Equal(DrawdownLevel.Normal, current);
        Assert.Equal(0m, pct);
        _brokerMock.DidNotReceive().GetAccountAsync(Arg.Any<CancellationToken>());
    }

    // ─── Transitions reported ───────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_ReportsTransition_WhenLevelChanges()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(97_000m); // 3% → Warning
        var monitor = CreateMonitor(DefaultOptions());

        var (previous, current, _) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Normal, previous);
        Assert.Equal(DrawdownLevel.Warning, current);
    }

    [Fact]
    public async Task UpdateAsync_ReportsSameLevel_WhenNoChange()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(100_000m);
        var monitor = CreateMonitor(DefaultOptions());

        var (previous, current, _) = await monitor.UpdateAsync();

        Assert.Equal(DrawdownLevel.Normal, previous);
        Assert.Equal(DrawdownLevel.Normal, current);
    }

    // ─── RiskManager integration ────────────────────────────────────────────────

    [Fact]
    public async Task RiskManager_ThrowsOnEmergencyDrawdown()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddHours(7), DateTimeOffset.UtcNow));
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo("test", 50_000m, 0m, 90_000m, 0m, true, false, DateTimeOffset.UtcNow));
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "0");
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");
        await fixture.StateRepository.SaveCircuitBreakerCountAsync(0);

        // Set monitor to Emergency
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(90_000m); // 10% → Emergency
        var monitor = CreateMonitor(DefaultOptions());
        await monitor.UpdateAsync();

        var options = DefaultOptions();
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, options, _riskLogger,
            drawdownMonitor: monitor);

        var ex = await Assert.ThrowsAsync<RiskManagerException>(
            () => riskManager.CheckSignalAsync(CreateSignal()).AsTask());

        Assert.Contains("Drawdown emergency", ex.Message);
    }

    [Fact]
    public async Task RiskManager_ThrowsOnHaltDrawdown()
    {
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(true, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow.AddHours(7), DateTimeOffset.UtcNow));
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo("test", 50_000m, 0m, 95_000m, 0m, true, false, DateTimeOffset.UtcNow));
        _brokerMock.GetPositionsAsync(Arg.Any<CancellationToken>())
            .Returns(new List<PositionInfo>());
        await fixture.StateRepository.SetStateAsync("daily_realized_pnl", "0");
        await fixture.StateRepository.SetStateAsync("daily_trade_count", "0");
        await fixture.StateRepository.SaveCircuitBreakerCountAsync(0);

        // Set monitor to Halt
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);
        SetupEquity(95_000m); // 5% → Halt
        var monitor = CreateMonitor(DefaultOptions());
        await monitor.UpdateAsync();

        var options = DefaultOptions();
        var riskManager = new RiskManager(_brokerMock, fixture.StateRepository, options, _riskLogger,
            drawdownMonitor: monitor);

        var ex = await Assert.ThrowsAsync<RiskManagerException>(
            () => riskManager.CheckSignalAsync(CreateSignal()).AsTask());

        Assert.Contains("Drawdown halt", ex.Message);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private DrawdownMonitor CreateMonitor(TradingOptions options) =>
        new(_brokerMock, fixture.StateRepository, options, _monitorLogger);

    private void SetupEquity(decimal portfolioValue) =>
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo("test", portfolioValue, 0m, portfolioValue, 0m, true, false, DateTimeOffset.UtcNow));

    private static SignalEvent CreateSignal() => new(
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
}
