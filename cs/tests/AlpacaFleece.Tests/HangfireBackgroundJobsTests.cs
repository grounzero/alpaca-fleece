using System.Globalization;
using AlpacaFleece.Worker.Jobs;
using Hangfire;

namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for HangfireBackgroundJobs (equity snapshots, daily resets, circuit breaker resets).
/// Covers success paths, skip logic, cancellation, and persistence verification.
/// </summary>
[Collection("Trading Database Collection")]
public sealed class HangfireBackgroundJobsTests(TradingFixture fixture) : IAsyncLifetime
{
    private readonly IBrokerService _brokerMock = Substitute.For<IBrokerService>();
    private readonly ILogger<HangfireBackgroundJobs> _logger = Substitute.For<ILogger<HangfireBackgroundJobs>>();
    private readonly IServiceProvider _serviceProviderMock = Substitute.For<IServiceProvider>();
    private readonly IServiceScope _scopeMock = Substitute.For<IServiceScope>();
    private HangfireBackgroundJobs _jobs = null!;

    public async Task InitializeAsync()
    {
        // Setup service provider mock to return scope directly
        var scopeFactoryMock = Substitute.For<IServiceScopeFactory>();
        scopeFactoryMock.CreateScope().Returns(_scopeMock);
        
        _scopeMock.ServiceProvider.Returns(_serviceProviderMock);
        _serviceProviderMock.GetService(typeof(IServiceScopeFactory)).Returns(scopeFactoryMock);
        _serviceProviderMock.GetService(typeof(IBrokerService)).Returns(_brokerMock);
        _serviceProviderMock.GetService(typeof(IStateRepository)).Returns(fixture.StateRepository);

        // Setup broker mock defaults
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns(new AccountInfo(
                AccountId: "test_account",
                CashAvailable: 50000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: DateTimeOffset.UtcNow));

        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(1),
                NextClose: DateTimeOffset.UtcNow.AddHours(6),
                FetchedAt: DateTimeOffset.UtcNow));

        // Create jobs without health check service (it's optional)
        _jobs = new HangfireBackgroundJobs(_serviceProviderMock, _logger);

        await Task.CompletedTask;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    #region EquitySnapshotJobAsync Tests

    [Fact]
    public async Task EquitySnapshotJobAsync_Success_PersistsSnapshot()
    {
        // Arrange - use InvariantCulture for culture-independent test
        var dailyPnl = 1234.56m;
        await fixture.StateRepository.SetStateAsync(
            "daily_realized_pnl", 
            dailyPnl.ToString(CultureInfo.InvariantCulture), 
            CancellationToken.None);
        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);

        // Act
        await _jobs.EquitySnapshotJobAsync(cancellationToken);

        // Assert: Verify equity snapshot was persisted with correct daily PnL
        var snapshots = await fixture.DbContext.EquityCurve
            .OrderByDescending(e => e.Id)
            .FirstOrDefaultAsync();

        Assert.NotNull(snapshots);
        Assert.Equal(100000m, snapshots.PortfolioValue);
        Assert.Equal(50000m, snapshots.CashBalance);
        Assert.Equal(dailyPnl, snapshots.DailyPnl);
    }

    [Fact]
    public async Task EquitySnapshotJobAsync_Success_RecordsReconciliationReport()
    {
        // Arrange - use InvariantCulture for culture-independent test
        var dailyPnl = 500.00m;
        await fixture.StateRepository.SetStateAsync(
            "daily_realized_pnl", 
            dailyPnl.ToString(CultureInfo.InvariantCulture), 
            CancellationToken.None);
        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);

        // Act
        await _jobs.EquitySnapshotJobAsync(cancellationToken);

        // Assert: Verify reconciliation report was inserted (Status field contains JSON)
        var reports = await fixture.DbContext.ReconciliationReports
            .Where(r => r.Status.Contains("equity-snapshot"))
            .ToListAsync();

        Assert.NotEmpty(reports);
        var latestReport = reports.OrderByDescending(r => r.Id).First();
        Assert.Contains("SUCCESS", latestReport.Status);
        Assert.Contains("100000.00", latestReport.Status);
        Assert.Contains("500.00", latestReport.Status);
    }

    [Fact]
    public async Task EquitySnapshotJobAsync_Cancelled_ThrowsException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(cts.Token);

        // Act & Assert - Repository wraps TaskCanceledException in StateRepositoryException
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await _jobs.EquitySnapshotJobAsync(cancellationToken));
        
        // Verify it's either the wrapper or the cancellation exception itself
        Assert.True(
            exception is OperationCanceledException || 
            exception is StateRepositoryException,
            $"Expected cancellation-related exception, got {exception.GetType().Name}");
    }

    [Fact]
    public async Task EquitySnapshotJobAsync_BrokerFailure_Throws()
    {
        // Arrange
        _brokerMock.GetAccountAsync(Arg.Any<CancellationToken>())
            .Returns<AccountInfo>(_ => throw new InvalidOperationException("Broker unavailable"));

        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _jobs.EquitySnapshotJobAsync(cancellationToken));
    }

    #endregion

    #region DailyResetJobAsync Tests

    [Fact]
    public async Task DailyResetJobAsync_FirstRun_AcquiresAndResets()
    {
        // Arrange
        var etZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var todayStr = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, etZone).ToString("yyyy-MM-dd");

        // Ensure no previous reset
        await fixture.StateRepository.SetStateAsync("daily_reset_date", "2020-01-01", CancellationToken.None);

        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);

        // Act
        await _jobs.DailyResetJobAsync(cancellationToken);

        // Assert: Verify reset date was updated
        var resetDate = await fixture.StateRepository.GetStateAsync("daily_reset_date", CancellationToken.None);
        Assert.Equal(todayStr, resetDate);

        // Verify reconciliation report shows SUCCESS
        var reports = await fixture.DbContext.ReconciliationReports
            .Where(r => r.Status.Contains("daily-reset"))
            .ToListAsync();

        Assert.NotEmpty(reports);
        var latestReport = reports.OrderByDescending(r => r.Id).First();
        Assert.Contains("SUCCESS", latestReport.Status);
        Assert.Contains(todayStr, latestReport.Status);
    }

    [Fact]
    public async Task DailyResetJobAsync_SecondRun_SkipsAndRecords()
    {
        // Arrange
        // Run first reset to set today's date
        var cancellationToken1 = Substitute.For<IJobCancellationToken>();
        cancellationToken1.ShutdownToken.Returns(CancellationToken.None);
        await _jobs.DailyResetJobAsync(cancellationToken1);

        // Act: Run second time
        var cancellationToken2 = Substitute.For<IJobCancellationToken>();
        cancellationToken2.ShutdownToken.Returns(CancellationToken.None);
        await _jobs.DailyResetJobAsync(cancellationToken2);

        // Assert: Verify reconciliation report shows SKIPPED
        var skippedReports = await fixture.DbContext.ReconciliationReports
            .Where(r => r.Status.Contains("daily-reset") && r.Status.Contains("SKIPPED"))
            .ToListAsync();

        Assert.NotEmpty(skippedReports);
        var latestReport = skippedReports.OrderByDescending(r => r.Id).First();
        Assert.Contains("SKIPPED", latestReport.Status);
        Assert.Contains("Already reset today", latestReport.Status);
    }

    [Fact]
    public async Task DailyResetJobAsync_Cancelled_ThrowsException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(cts.Token);

        // Act & Assert - Repository wraps TaskCanceledException in StateRepositoryException
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await _jobs.DailyResetJobAsync(cancellationToken));
        
        // Verify it's either the wrapper or the cancellation exception itself
        Assert.True(
            exception is OperationCanceledException || 
            exception is StateRepositoryException,
            $"Expected cancellation-related exception, got {exception.GetType().Name}");
    }

    #endregion

    #region CircuitBreakerResetJobAsync Tests

    [Fact]
    public async Task CircuitBreakerResetJobAsync_MarketOpen_ResetsCount()
    {
        // Arrange
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: true,
                NextOpen: DateTimeOffset.UtcNow.AddHours(1),
                NextClose: DateTimeOffset.UtcNow.AddHours(6),
                FetchedAt: DateTimeOffset.UtcNow));

        await fixture.StateRepository.SaveCircuitBreakerCountAsync(5, CancellationToken.None);

        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);

        // Act
        await _jobs.CircuitBreakerResetJobAsync(cancellationToken);

        // Assert: Verify circuit breaker count was reset to 0
        var count = await fixture.StateRepository.GetCircuitBreakerCountAsync(CancellationToken.None);
        Assert.Equal(0, count);

        // Verify reconciliation report shows SUCCESS
        var reports = await fixture.DbContext.ReconciliationReports
            .Where(r => r.Status.Contains("circuit-breaker-reset"))
            .ToListAsync();

        Assert.NotEmpty(reports);
        var latestReport = reports.OrderByDescending(r => r.Id).First();
        Assert.Contains("SUCCESS", latestReport.Status);
        Assert.Contains("reset to 0", latestReport.Status);
    }

    [Fact]
    public async Task CircuitBreakerResetJobAsync_MarketClosed_SkipsAndRecords()
    {
        // Arrange
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns(new ClockInfo(
                IsOpen: false,
                NextOpen: DateTimeOffset.UtcNow.AddHours(8),
                NextClose: DateTimeOffset.UtcNow.AddHours(14),
                FetchedAt: DateTimeOffset.UtcNow));

        await fixture.StateRepository.SaveCircuitBreakerCountAsync(5, CancellationToken.None);

        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);

        // Act
        await _jobs.CircuitBreakerResetJobAsync(cancellationToken);

        // Assert: Verify circuit breaker count was NOT reset
        var count = await fixture.StateRepository.GetCircuitBreakerCountAsync(CancellationToken.None);
        Assert.Equal(5, count);

        // Verify reconciliation report shows SKIPPED
        var reports = await fixture.DbContext.ReconciliationReports
            .Where(r => r.Status.Contains("circuit-breaker-reset") && r.Status.Contains("SKIPPED"))
            .ToListAsync();

        Assert.NotEmpty(reports);
        var latestReport = reports.OrderByDescending(r => r.Id).First();
        Assert.Contains("SKIPPED", latestReport.Status);
        Assert.Contains("Market not open", latestReport.Status);
    }

    [Fact]
    public async Task CircuitBreakerResetJobAsync_Cancelled_ThrowsException()
    {
        // Arrange
        var cts = new CancellationTokenSource();
        cts.Cancel();
        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(cts.Token);

        // Act & Assert - Repository wraps TaskCanceledException in StateRepositoryException
        var exception = await Assert.ThrowsAnyAsync<Exception>(
            async () => await _jobs.CircuitBreakerResetJobAsync(cancellationToken));
        
        // Verify it's either the wrapper or the cancellation exception itself
        Assert.True(
            exception is OperationCanceledException || 
            exception is StateRepositoryException,
            $"Expected cancellation-related exception, got {exception.GetType().Name}");
    }

    [Fact]
    public async Task CircuitBreakerResetJobAsync_BrokerFailure_Throws()
    {
        // Arrange
        _brokerMock.GetClockAsync(Arg.Any<CancellationToken>())
            .Returns<ClockInfo>(_ => throw new InvalidOperationException("Clock API failed"));

        var cancellationToken = Substitute.For<IJobCancellationToken>();
        cancellationToken.ShutdownToken.Returns(CancellationToken.None);

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _jobs.CircuitBreakerResetJobAsync(cancellationToken));
    }

    #endregion
}
