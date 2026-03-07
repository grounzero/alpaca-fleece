namespace AlpacaFleece.Tests;

/// <summary>
/// Tests for StateRepository (gate atomicity, KV crud, circuit breaker).
/// </summary>
[Collection("Trading Database Collection")]
public sealed class StateRepositoryTests(TradingFixture fixture)
{
    private readonly ILogger<StateRepository> _logger = Substitute.For<ILogger<StateRepository>>();

    [Fact]
    public async Task GetStateAsync_ReturnsNullForMissingKey()
    {
        var repo = fixture.StateRepository;
        var value = await repo.GetStateAsync("missing_key");

        Assert.Null(value);
    }

    [Fact]
    public async Task SetStateAsync_StoresValue()
    {
        var repo = fixture.StateRepository;
        await repo.SetStateAsync("test_key", "test_value");

        var value = await repo.GetStateAsync("test_key");
        Assert.Equal("test_value", value);
    }

    [Fact]
    public async Task SetStateAsync_UpdatesExistingValue()
    {
        var repo = fixture.StateRepository;
        await repo.SetStateAsync("test_key", "value1");
        await repo.SetStateAsync("test_key", "value2");

        var value = await repo.GetStateAsync("test_key");
        Assert.Equal("value2", value);
    }

    [Fact]
    public async Task GateTryAcceptAsync_AcceptsFirstTime()
    {
        var repo = fixture.StateRepository;
        var barTs = DateTimeOffset.UtcNow;
        var nowUtc = DateTimeOffset.UtcNow;

        var accepted = await repo.GateTryAcceptAsync(
            "test_gate_first",
            barTs,
            nowUtc,
            TimeSpan.FromSeconds(1));

        Assert.True(accepted);
    }

    [Fact]
    public async Task GateTryAcceptAsync_RejectsSameBarDuplicate()
    {
        var repo = fixture.StateRepository;
        var barTs = DateTimeOffset.UtcNow;
        var nowUtc = DateTimeOffset.UtcNow;

        // First attempt succeeds
        var accepted1 = await repo.GateTryAcceptAsync(
            "test_gate_duplicate",
            barTs,
            nowUtc,
            TimeSpan.FromSeconds(1));

        // Second attempt with same barTs should fail
        var accepted2 = await repo.GateTryAcceptAsync(
            "test_gate_duplicate",
            barTs,
            nowUtc.AddSeconds(0.1),
            TimeSpan.FromSeconds(1));

        Assert.True(accepted1);
        Assert.False(accepted2);
    }

    [Fact]
    public async Task GateTryAcceptAsync_RespectsCooldown()
    {
        var repo = fixture.StateRepository;
        var barTs1 = DateTimeOffset.UtcNow;
        var barTs2 = barTs1.AddSeconds(1);
        var nowUtc = DateTimeOffset.UtcNow;

        // First attempt succeeds
        var accepted1 = await repo.GateTryAcceptAsync(
            "test_gate_cooldown",
            barTs1,
            nowUtc,
            TimeSpan.FromSeconds(5));

        // Second attempt immediately after should fail due to cooldown
        var accepted2 = await repo.GateTryAcceptAsync(
            "test_gate_cooldown",
            barTs2,
            nowUtc.AddSeconds(1),
            TimeSpan.FromSeconds(5));

        Assert.True(accepted1);
        Assert.False(accepted2);
    }

    [Fact]
    public async Task GateTryAcceptAsync_AcceptsAfterCooldown()
    {
        var repo = fixture.StateRepository;
        var barTs1 = DateTimeOffset.UtcNow;
        var barTs2 = barTs1.AddSeconds(10);
        var nowUtc = DateTimeOffset.UtcNow;

        // First attempt succeeds
        var accepted1 = await repo.GateTryAcceptAsync(
            "test_gate_after",
            barTs1,
            nowUtc,
            TimeSpan.FromSeconds(5));

        // Second attempt after cooldown should succeed
        var accepted2 = await repo.GateTryAcceptAsync(
            "test_gate_after",
            barTs2,
            nowUtc.AddSeconds(6),
            TimeSpan.FromSeconds(5));

        Assert.True(accepted1);
        Assert.True(accepted2);
    }

    [Fact]
    public async Task SaveOrderIntentAsync_PersistsIntent()
    {
        var repo = fixture.StateRepository;
        var clientOrderId = "test_order_123";

        await repo.SaveOrderIntentAsync(
            clientOrderId,
            "AAPL",
            "BUY",
            100,
            150m,
            DateTimeOffset.UtcNow);

        var intent = await repo.GetOrderIntentAsync(clientOrderId);

        Assert.NotNull(intent);
        Assert.Equal(clientOrderId, intent.ClientOrderId);
        Assert.Equal("AAPL", intent.Symbol);
        Assert.Equal("BUY", intent.Side);
        Assert.Equal(100, intent.Quantity);
        Assert.Equal(150m, intent.LimitPrice);
    }

    [Fact]
    public async Task UpdateOrderIntentAsync_UpdatesStatus()
    {
        var repo = fixture.StateRepository;
        var clientOrderId = "test_order_456";

        await repo.SaveOrderIntentAsync(
            clientOrderId,
            "AAPL",
            "BUY",
            100,
            150m,
            DateTimeOffset.UtcNow);

        await repo.UpdateOrderIntentAsync(
            clientOrderId,
            "alpaca_123",
            OrderState.Filled,
            DateTimeOffset.UtcNow);

        var intent = await repo.GetOrderIntentAsync(clientOrderId);

        Assert.NotNull(intent);
        Assert.Equal("alpaca_123", intent.AlpacaOrderId);
        Assert.Equal(OrderState.Filled, intent.Status);
    }

    [Fact]
    public async Task InsertFillIdempotentAsync_InsertsUniqueFill()
    {
        var repo = fixture.StateRepository;

        await repo.InsertFillIdempotentAsync(
            "alpaca_order_1",
            "client_order_1",
            50,
            150m,
            "dedupe_key_1",
            DateTimeOffset.UtcNow);

        var fills = fixture.DbContext.Fills.ToList();
        Assert.Single(fills);
    }

    [Fact]
    public async Task InsertFillIdempotentAsync_IgnoresDuplicate()
    {
        var repo = fixture.StateRepository;

        await repo.InsertFillIdempotentAsync(
            "alpaca_order_1",
            "client_order_1",
            50,
            150m,
            "dedupe_key_1",
            DateTimeOffset.UtcNow);

        await repo.InsertFillIdempotentAsync(
            "alpaca_order_1",
            "client_order_1",
            50,
            150m,
            "dedupe_key_1",
            DateTimeOffset.UtcNow);

        var fills = fixture.DbContext.Fills.ToList();
        Assert.Single(fills);
    }

    [Fact]
    public async Task GetCircuitBreakerCountAsync_ReturnsCount()
    {
        var repo = fixture.StateRepository;

        await repo.SaveCircuitBreakerCountAsync(5);
        var count = await repo.GetCircuitBreakerCountAsync();

        Assert.Equal(5, count);
    }

    [Fact]
    public async Task ResetDailyStateAsync_ClearsCircuitBreaker()
    {
        var repo = fixture.StateRepository;

        await repo.SaveCircuitBreakerCountAsync(5);
        await repo.ResetDailyStateAsync();
        var count = await repo.GetCircuitBreakerCountAsync();

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task RecordExitAttemptFailureAsync_IncrementsAttemptCount_And_EscalatesBackoff()
    {
        // Arrange
        var repo = fixture.StateRepository;
        var symbol = "QQQ";

        // First attempt (initializes AttemptCount = 1, backoff = 2^(1-1) = 1)
        await repo.RecordExitAttemptAsync(symbol);
        var attempt1 = await repo.GetExitBackoffSecondsAsync(symbol);
        Assert.Equal(1, attempt1); // Initial backoff after first attempt

        // Record first failure (increments to AttemptCount = 2, backoff = 2^(2-1) = 2)
        await repo.RecordExitAttemptFailureAsync(symbol);
        var backoff1 = await repo.GetExitBackoffSecondsAsync(symbol);
        Assert.Equal(2, backoff1); // Backoff escalates to 2 seconds

        // Record second failure (increments to AttemptCount = 3, backoff = 2^(3-1) = 4)
        await repo.RecordExitAttemptFailureAsync(symbol);
        var backoff2 = await repo.GetExitBackoffSecondsAsync(symbol);
        Assert.Equal(4, backoff2); // Backoff escalates to 4 seconds

        // Record third failure (increments to AttemptCount = 4, backoff = 2^(4-1) = 8)
        await repo.RecordExitAttemptFailureAsync(symbol);
        var backoff3 = await repo.GetExitBackoffSecondsAsync(symbol);
        Assert.Equal(8, backoff3); // Backoff escalates to 8 seconds

        // Verify escalation continues and caps at 300s
        for (int i = 0; i < 10; i++)
        {
            await repo.RecordExitAttemptFailureAsync(symbol);
        }
        var backoffCapped = await repo.GetExitBackoffSecondsAsync(symbol);
        Assert.Equal(300, backoffCapped); // Capped at max
    }
}
