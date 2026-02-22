namespace AlpacaFleece.Infrastructure.Broker;

/// <summary>
/// Alpaca broker service with dual-gate protection, cache for account/positions, and Polly retry on reads only.
/// Clock is NEVER cached.
/// </summary>
public sealed class AlpacaBrokerService(
    BrokerOptions options,
    ILogger<AlpacaBrokerService> logger) : IBrokerService
{
    private readonly SemaphoreSlim _accountCacheLock = new(1, 1);
    private readonly SemaphoreSlim _positionsCacheLock = new(1, 1);
    private AccountInfo? _accountCache;
    private DateTimeOffset _accountCacheTime = DateTimeOffset.MinValue;
    private IReadOnlyList<PositionInfo>? _positionsCache;
    private DateTimeOffset _positionsCacheTime = DateTimeOffset.MinValue;
    private const int CacheTtlMs = 1000; // 1 second TTL

    /// <summary>
    /// Gets market clock (ALWAYS fresh, never cached).
    /// </summary>
    public async ValueTask<ClockInfo> GetClockAsync(CancellationToken ct = default)
    {
        try
        {
            // Placeholder: in Phase 2 this will call actual Alpaca API
            // For now, return mock data for testing
            var now = DateTimeOffset.UtcNow;
            var clock = new ClockInfo(
                IsOpen: true,
                NextOpen: now.AddDays(1),
                NextClose: now.AddHours(7),
                FetchedAt: now);

            return clock;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch clock");
            throw new BrokerTransientException("Failed to fetch clock", ex);
        }
    }

    /// <summary>
    /// Gets account info with 1s TTL cache.
    /// </summary>
    public async ValueTask<AccountInfo> GetAccountAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Check if cache is still valid
        if (_accountCache != null && (now - _accountCacheTime).TotalMilliseconds < CacheTtlMs)
        {
            return _accountCache;
        }

        await _accountCacheLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_accountCache != null && (now - _accountCacheTime).TotalMilliseconds < CacheTtlMs)
            {
                return _accountCache;
            }

            // Placeholder: in Phase 2 this will call actual Alpaca API with Polly retry
            var account = new AccountInfo(
                AccountId: "test-account",
                CashAvailable: 100000m,
                CashReserved: 0m,
                PortfolioValue: 100000m,
                DayTradeCount: 0m,
                IsTradable: true,
                IsAccountRestricted: false,
                FetchedAt: now);

            _accountCache = account;
            _accountCacheTime = now;
            return account;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch account");
            throw new BrokerTransientException("Failed to fetch account", ex);
        }
        finally
        {
            _accountCacheLock.Release();
        }
    }

    /// <summary>
    /// Gets positions with 1s TTL cache.
    /// </summary>
    public async ValueTask<IReadOnlyList<PositionInfo>> GetPositionsAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Check if cache is still valid
        if (_positionsCache != null && (now - _positionsCacheTime).TotalMilliseconds < CacheTtlMs)
        {
            return _positionsCache;
        }

        await _positionsCacheLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock
            if (_positionsCache != null && (now - _positionsCacheTime).TotalMilliseconds < CacheTtlMs)
            {
                return _positionsCache;
            }

            // Placeholder: in Phase 2 this will call actual Alpaca API with Polly retry
            var positions = new List<PositionInfo>();
            _positionsCache = positions.AsReadOnly();
            _positionsCacheTime = now;
            return _positionsCache;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch positions");
            throw new BrokerTransientException("Failed to fetch positions", ex);
        }
        finally
        {
            _positionsCacheLock.Release();
        }
    }

    /// <summary>
    /// Submits an order (no retry).
    /// </summary>
    public async ValueTask<OrderInfo> SubmitOrderAsync(
        string symbol,
        string side,
        int quantity,
        decimal limitPrice,
        string clientOrderId,
        CancellationToken ct = default)
    {
        try
        {
            if (options.KillSwitch)
                throw new BrokerFatalException("Kill switch is active");

            if (options.DryRun)
            {
                logger.LogInformation(
                    "DRY RUN: Would submit {side} order for {qty} of {symbol} at {price}",
                    side, quantity, symbol, limitPrice);

                return new OrderInfo(
                    AlpacaOrderId: $"dry-{clientOrderId}",
                    ClientOrderId: clientOrderId,
                    Symbol: symbol,
                    Side: side,
                    Quantity: quantity,
                    FilledQuantity: 0,
                    AverageFilledPrice: 0m,
                    Status: OrderState.Accepted,
                    CreatedAt: DateTimeOffset.UtcNow,
                    UpdatedAt: null);
            }

            // Placeholder: in Phase 2 this will call actual Alpaca API
            return new OrderInfo(
                AlpacaOrderId: $"test-{clientOrderId}",
                ClientOrderId: clientOrderId,
                Symbol: symbol,
                Side: side,
                Quantity: quantity,
                FilledQuantity: 0,
                AverageFilledPrice: 0m,
                Status: OrderState.PendingNew,
                CreatedAt: DateTimeOffset.UtcNow,
                UpdatedAt: null);
        }
        catch (BrokerFatalException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit order");
            throw new BrokerFatalException("Failed to submit order", ex);
        }
    }

    /// <summary>
    /// Cancels an order (no retry).
    /// </summary>
    public async ValueTask CancelOrderAsync(string alpacaOrderId, CancellationToken ct = default)
    {
        try
        {
            if (options.DryRun)
            {
                logger.LogInformation("DRY RUN: Would cancel order {orderId}", alpacaOrderId);
                return;
            }

            // Placeholder: in Phase 2 this will call actual Alpaca API
            logger.LogInformation("Cancelled order {orderId}", alpacaOrderId);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel order");
            throw new BrokerFatalException("Failed to cancel order", ex);
        }
    }

    /// <summary>
    /// Gets all open orders (with Polly retry).
    /// </summary>
    public async ValueTask<IReadOnlyList<OrderInfo>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        try
        {
            // Placeholder: in Phase 2 this will call actual Alpaca API with Polly retry
            return new List<OrderInfo>().AsReadOnly();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch open orders");
            throw new BrokerTransientException("Failed to fetch open orders", ex);
        }
    }
}
