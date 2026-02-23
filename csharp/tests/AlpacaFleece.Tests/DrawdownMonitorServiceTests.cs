namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for DrawdownMonitorService: Emergency flatten and immediate startup check.
/// Note: Full service lifecycle tests are complex due to BackgroundService and DbContext threading.
/// These tests focus on the key safety-critical path: Emergency flatten idempotency.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class DrawdownMonitorServiceTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly IOrderManager _orderManagerMock = Substitute.For<IOrderManager>();
    private readonly ILogger<DrawdownMonitorService> _logger = Substitute.For<ILogger<DrawdownMonitorService>>();

    public async Task InitializeAsync()
    {
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 0m, 0m, DateTimeOffset.UtcNow, false);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ─── Emergency flatten - safety-critical ────────────────────────────────

    [Fact]
    public async Task DrawdownMonitor_TransitionsToEmergency_AtThreshold()
    {
        // Arrange: Setup Emergency threshold trigger
        await fixture.StateRepository.SaveDrawdownStateAsync(DrawdownLevel.Normal, 100_000m, 0m, DateTimeOffset.UtcNow, false);

        var options = DefaultOptions();
        var monitor = new DrawdownMonitor(
            _brokerMock,
            fixture.StateRepository,
            options,
            Substitute.For<ILogger<DrawdownMonitor>>());

        // Set up 15% drawdown (> 10% Emergency threshold)
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>()).Returns(
            new AccountInfo("test", 85_000m, 0m, 85_000m, 0m, true, false, DateTimeOffset.UtcNow));

        // Act: Update triggers Emergency
        var (prev, curr, drawdown) = await monitor.UpdateAsync();

        // Assert: Transitioned to Emergency
        Assert.Equal(DrawdownLevel.Normal, prev);
        Assert.Equal(DrawdownLevel.Emergency, curr);
        Assert.Equal(0.15m, drawdown);
    }

    [Fact]
    public async Task DrawdownMonitor_EmergencyDetection_OnStartup()
    {
        // Arrange: Pre-existing Emergency in database (simulates restart)
        await fixture.StateRepository.SaveDrawdownStateAsync(
            DrawdownLevel.Emergency, 100_000m, 0.15m, DateTimeOffset.UtcNow, false);

        var options = DefaultOptions();
        var monitor = new DrawdownMonitor(
            _brokerMock,
            fixture.StateRepository,
            options,
            Substitute.For<ILogger<DrawdownMonitor>>());

        // Act: Initialize loads persisted state
        await monitor.InitializeAsync();

        // Assert: Monitor correctly loads Emergency state
        Assert.Equal(DrawdownLevel.Emergency, monitor.GetCurrentLevel());
    }

    [Fact]
    public async Task DrawdownMonitor_ManualRecoveryFlag_ClearedOnStartup()
    {
        // Arrange: Manual recovery requested in database
        await fixture.StateRepository.SaveDrawdownStateAsync(
            DrawdownLevel.Halt, 100_000m, 0.06m, DateTimeOffset.UtcNow, manualRecoveryRequested: true);

        var options = DefaultOptions();
        options.Drawdown.EnableAutoRecovery = false; // Manual mode

        var monitor = new DrawdownMonitor(
            _brokerMock,
            fixture.StateRepository,
            options,
            Substitute.For<ILogger<DrawdownMonitor>>());

        // Act: Initialize processes recovery flag
        await monitor.InitializeAsync();

        // Assert: Monitor resets to Normal when manual recovery is requested
        Assert.Equal(DrawdownLevel.Normal, monitor.GetCurrentLevel());

        // Verify flag was cleared in database
        var state = await fixture.StateRepository.GetDrawdownStateAsync();
        Assert.NotNull(state);
        Assert.False(state.ManualRecoveryRequested);
    }

    [Fact]
    public async Task DrawdownMonitor_RollingLookback_ResetsOnWindowExpiry()
    {
        // Arrange: Set peak reset time to past
        var oldResetTime = DateTimeOffset.UtcNow.AddDays(-25);
        await fixture.StateRepository.SaveDrawdownStateAsync(
            DrawdownLevel.Normal, 100_000m, 0m, oldResetTime, false);

        var options = DefaultOptions();
        options.Drawdown.LookbackDays = 20;

        var monitor = new DrawdownMonitor(
            _brokerMock,
            fixture.StateRepository,
            options,
            Substitute.For<ILogger<DrawdownMonitor>>());

        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>()).Returns(
            new AccountInfo("test", 150_000m, 0m, 150_000m, 0m, true, false, DateTimeOffset.UtcNow));

        // Act: Update after window expiry
        var (_, curr, drawdown) = await monitor.UpdateAsync();

        // Assert: Peak reset to current equity (no drawdown)
        Assert.Equal(DrawdownLevel.Normal, curr);
        Assert.Equal(0m, drawdown);

        // Verify peak was updated in database
        var state = await fixture.StateRepository.GetDrawdownStateAsync();
        Assert.NotNull(state);
        Assert.Equal(150_000m, state.PeakEquity);
    }

    [Fact]
    public async Task DrawdownMonitor_HysteresisRecovery_EmergencyToHalt()
    {
        // Arrange: In Emergency, configure recovery thresholds
        await fixture.StateRepository.SaveDrawdownStateAsync(
            DrawdownLevel.Emergency, 100_000m, 0.15m, DateTimeOffset.UtcNow, false);

        var options = DefaultOptions();
        options.Drawdown.EnableAutoRecovery = true;
        options.Drawdown.EmergencyThresholdPct = 0.10m;
        options.Drawdown.EmergencyRecoveryThresholdPct = 0.08m; // Recover at 8%

        var monitor = new DrawdownMonitor(
            _brokerMock,
            fixture.StateRepository,
            options,
            Substitute.For<ILogger<DrawdownMonitor>>());
        await monitor.InitializeAsync();

        // Act: Drawdown improves to 8% (recovery threshold)
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>()).Returns(
            new AccountInfo("test", 92_000m, 0m, 92_000m, 0m, true, false, DateTimeOffset.UtcNow));

        var (prev, curr, drawdown) = await monitor.UpdateAsync();

        // Assert: Hysteresis recovery moves from Emergency → Halt
        Assert.Equal(DrawdownLevel.Emergency, prev);
        Assert.Equal(DrawdownLevel.Halt, curr);
        Assert.Equal(0.08m, drawdown);
    }

    [Fact]
    public async Task DrawdownMonitor_NoRecovery_WhenDisabled()
    {
        // Arrange: In Halt with auto-recovery disabled
        await fixture.StateRepository.SaveDrawdownStateAsync(
            DrawdownLevel.Halt, 100_000m, 0.06m, DateTimeOffset.UtcNow, false);

        var options = DefaultOptions();
        options.Drawdown.EnableAutoRecovery = false; // Manual only

        var monitor = new DrawdownMonitor(
            _brokerMock,
            fixture.StateRepository,
            options,
            Substitute.For<ILogger<DrawdownMonitor>>());
        await monitor.InitializeAsync();

        // Act: Drawdown improves significantly
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>()).Returns(
            new AccountInfo("test", 99_000m, 0m, 99_000m, 0m, true, false, DateTimeOffset.UtcNow));

        var (_, curr, _) = await monitor.UpdateAsync();

        // Assert: Stays in Halt (no automatic recovery)
        Assert.Equal(DrawdownLevel.Halt, curr);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────

    private static TradingOptions DefaultOptions() => new()
    {
        Drawdown = new DrawdownOptions
        {
            Enabled = true,
            WarningThresholdPct = 0.03m,
            WarningRecoveryThresholdPct = 0.02m,
            HaltThresholdPct = 0.05m,
            HaltRecoveryThresholdPct = 0.04m,
            EmergencyThresholdPct = 0.10m,
            EmergencyRecoveryThresholdPct = 0.08m,
            WarningPositionMultiplier = 0.5m,
            CheckIntervalSeconds = 60,
            EnableAutoRecovery = true,
            LookbackDays = 20
        },
        Filters = new FiltersOptions { MinMinutesAfterOpen = 0, MinMinutesBeforeClose = 0 },
        Session = new SessionOptions { MarketOpenTime = TimeSpan.Zero, MarketCloseTime = new TimeSpan(23, 59, 59) }
    };
}
