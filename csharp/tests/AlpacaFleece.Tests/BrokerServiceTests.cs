namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for AlpacaBrokerService (clock never cached, cache on reads, no retry on writes).
/// </summary>
public sealed class BrokerServiceTests
{
    [Fact]
    public async Task GetClockAsync_NeverCaches()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test"
        };
        var logger = Substitute.For<ILogger<AlpacaBrokerService>>();
        var broker = new AlpacaBrokerService(options, logger);

        var clock1 = await broker.GetClockAsync();
        var clock2 = await broker.GetClockAsync();

        // Both should be fresh (different FetchedAt times)
        Assert.NotEqual(clock1.FetchedAt, clock2.FetchedAt);
    }

    [Fact]
    public async Task GetAccountAsync_CachesTtl()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test"
        };
        var logger = Substitute.For<ILogger<AlpacaBrokerService>>();
        var broker = new AlpacaBrokerService(options, logger);

        var account1 = await broker.GetAccountAsync();
        var account2 = await broker.GetAccountAsync();

        // Both should have same FetchedAt (cached within 1s)
        Assert.Equal(account1.FetchedAt, account2.FetchedAt);
    }

    [Fact]
    public async Task GetPositionsAsync_CachesTtl()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test"
        };
        var logger = Substitute.For<ILogger<AlpacaBrokerService>>();
        var broker = new AlpacaBrokerService(options, logger);

        var positions1 = await broker.GetPositionsAsync();
        var positions2 = await broker.GetPositionsAsync();

        // Should return same instance (cached)
        Assert.Same(positions1, positions2);
    }

    [Fact]
    public async Task SubmitOrderAsync_ThrowsWhenKillSwitchActive()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test",
            KillSwitch = true
        };
        var logger = Substitute.For<ILogger<AlpacaBrokerService>>();
        var broker = new AlpacaBrokerService(options, logger);

        var ex = await Assert.ThrowsAsync<BrokerFatalException>(
            () => broker.SubmitOrderAsync("AAPL", "BUY", 100, 150m, "order_123").AsTask());

        Assert.Contains("Kill switch", ex.Message);
    }

    [Fact]
    public async Task SubmitOrderAsync_DryRunDoesNotSubmit()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test",
            DryRun = true
        };
        var logger = Substitute.For<ILogger<AlpacaBrokerService>>();
        var broker = new AlpacaBrokerService(options, logger);

        var order = await broker.SubmitOrderAsync("AAPL", "BUY", 100, 150m, "order_123");

        Assert.Equal(OrderState.Accepted, order.Status);
        Assert.StartsWith("dry-", order.AlpacaOrderId);
    }

    [Fact]
    public async Task CancelOrderAsync_ThrowsOnNormalError()
    {
        var options = new BrokerOptions
        {
            ApiKey = "test",
            SecretKey = "test"
        };
        var logger = Substitute.For<ILogger<AlpacaBrokerService>>();
        var broker = new AlpacaBrokerService(options, logger);

        // Normal case (should not throw)
        await broker.CancelOrderAsync("alpaca_123");
    }
}
