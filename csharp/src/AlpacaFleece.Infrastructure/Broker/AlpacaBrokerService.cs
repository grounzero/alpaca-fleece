using Alpaca.Markets;

namespace AlpacaFleece.Infrastructure.Broker;

/// <summary>
/// Alpaca broker service with dual-gate protection, cache for account/positions, and Polly retry on reads only.
/// Clock is NEVER cached.
/// </summary>
public sealed class AlpacaBrokerService(
    BrokerOptions options,
    IAlpacaTradingClient tradingClient,
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
            var clock = await tradingClient.GetClockAsync(ct);
            return new ClockInfo(
                IsOpen: clock.IsOpen,
                NextOpen: new DateTimeOffset(clock.NextOpenUtc, TimeSpan.Zero),
                NextClose: new DateTimeOffset(clock.NextCloseUtc, TimeSpan.Zero),
                FetchedAt: DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) { throw; }
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

        if (_accountCache != null && (now - _accountCacheTime).TotalMilliseconds < CacheTtlMs)
            return _accountCache;

        await _accountCacheLock.WaitAsync(ct);
        try
        {
            if (_accountCache != null && (now - _accountCacheTime).TotalMilliseconds < CacheTtlMs)
                return _accountCache;

            var account = await tradingClient.GetAccountAsync(ct);
            var info = new AccountInfo(
                AccountId: account.AccountId.ToString(),
                CashAvailable: account.TradableCash,
                CashReserved: 0m,
                PortfolioValue: account.Equity ?? account.TradableCash,
                DayTradeCount: (decimal)account.DayTradeCount,
                IsTradable: !account.IsTradingBlocked,
                IsAccountRestricted: account.IsAccountBlocked,
                FetchedAt: now);

            _accountCache = info;
            _accountCacheTime = now;
            return info;
        }
        catch (OperationCanceledException) { throw; }
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

        if (_positionsCache != null && (now - _positionsCacheTime).TotalMilliseconds < CacheTtlMs)
            return _positionsCache;

        await _positionsCacheLock.WaitAsync(ct);
        try
        {
            if (_positionsCache != null && (now - _positionsCacheTime).TotalMilliseconds < CacheTtlMs)
                return _positionsCache;

            var positions = await tradingClient.ListPositionsAsync(ct);
            var result = positions.Select(p => new PositionInfo(
                Symbol: p.Symbol,
                Quantity: (int)p.Quantity,
                AverageEntryPrice: p.AverageEntryPrice,
                CurrentPrice: p.AssetCurrentPrice ?? 0m,
                UnrealizedPnl: p.UnrealizedProfitLoss ?? 0m,
                UnrealizedPnlPercent: p.UnrealizedProfitLossPercent ?? 0m,
                FetchedAt: now)).ToList().AsReadOnly();

            _positionsCache = result;
            _positionsCacheTime = now;
            return result;
        }
        catch (OperationCanceledException) { throw; }
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
    /// Submits an order (no retry — order submission failures are treated as fatal).
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
                    "DRY RUN: Would submit {Side} order for {Qty} of {Symbol} at {Price}",
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

            var orderSide = side.Equals("buy", StringComparison.OrdinalIgnoreCase)
                ? OrderSide.Buy
                : OrderSide.Sell;

            var request = new NewOrderRequest(
                symbol,
                OrderQuantity.Fractional(quantity),
                orderSide,
                limitPrice > 0 ? OrderType.Limit : OrderType.Market,
                TimeInForce.Day)
            {
                LimitPrice = limitPrice > 0 ? limitPrice : null,
                ClientOrderId = clientOrderId,
            };

            var order = await tradingClient.PostOrderAsync(request, ct);

            // Invalidate positions cache so the next call fetches fresh data.
            // Prevents a concurrent signal from seeing the pre-trade position count within the 1 s TTL.
            _positionsCacheTime = DateTimeOffset.MinValue;

            logger.LogInformation(
                "Submitted {Side} order {OrderId} for {Qty} of {Symbol}",
                side, order.OrderId, quantity, symbol);
            return MapOrder(order);
        }
        catch (BrokerFatalException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to submit order for {Symbol}", symbol);
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
                logger.LogInformation("DRY RUN: Would cancel order {OrderId}", alpacaOrderId);
                return;
            }

            await tradingClient.CancelOrderAsync(Guid.Parse(alpacaOrderId), ct);
            logger.LogInformation("Cancelled order {OrderId}", alpacaOrderId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to cancel order {OrderId}", alpacaOrderId);
            throw new BrokerFatalException("Failed to cancel order", ex);
        }
    }

    /// <summary>
    /// Gets all open orders.
    /// </summary>
    public async ValueTask<IReadOnlyList<OrderInfo>> GetOpenOrdersAsync(CancellationToken ct = default)
    {
        try
        {
            var orders = await tradingClient.ListOrdersAsync(
                new ListOrdersRequest { OrderStatusFilter = OrderStatusFilter.Open }, ct);
            return orders.Select(MapOrder).ToList().AsReadOnly();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch open orders");
            throw new BrokerTransientException("Failed to fetch open orders", ex);
        }
    }

    /// <summary>
    /// Gets a single order by Alpaca order ID. Returns null if not found.
    /// </summary>
    public async ValueTask<OrderInfo?> GetOrderByIdAsync(string alpacaOrderId, CancellationToken ct = default)
    {
        try
        {
            if (!Guid.TryParse(alpacaOrderId, out var orderId))
            {
                logger.LogWarning("Invalid Alpaca order ID format: {OrderId}", alpacaOrderId);
                return null;
            }

            var order = await tradingClient.GetOrderAsync(orderId, ct);
            return order == null ? null : MapOrder(order);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (
            ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("404"))
        {
            logger.LogDebug("Order {OrderId} not found in Alpaca", alpacaOrderId);
            return null;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to fetch order {OrderId}", alpacaOrderId);
            throw new BrokerTransientException("Failed to fetch order", ex);
        }
    }

    private static OrderInfo MapOrder(IOrder order) => new(
        AlpacaOrderId: order.OrderId.ToString(),
        ClientOrderId: order.ClientOrderId ?? string.Empty,
        Symbol: order.Symbol,
        Side: order.OrderSide == OrderSide.Buy ? "buy" : "sell",
        Quantity: (int)order.IntegerQuantity,
        FilledQuantity: (int)order.IntegerFilledQuantity,
        AverageFilledPrice: order.AverageFillPrice ?? 0m,
        Status: MapOrderStatus(order.OrderStatus),
        CreatedAt: order.CreatedAtUtc.HasValue
            ? new DateTimeOffset(order.CreatedAtUtc.Value, TimeSpan.Zero)
            : DateTimeOffset.UtcNow,
        UpdatedAt: order.UpdatedAtUtc.HasValue
            ? new DateTimeOffset(order.UpdatedAtUtc.Value, TimeSpan.Zero)
            : null);

    private static OrderState MapOrderStatus(OrderStatus status) => status switch
    {
        OrderStatus.PendingNew       => OrderState.PendingNew,
        OrderStatus.New              => OrderState.PendingNew,
        OrderStatus.Held             => OrderState.PendingNew,
        OrderStatus.Accepted         => OrderState.Accepted,
        OrderStatus.AcceptedForBidding => OrderState.Accepted,
        OrderStatus.PartiallyFilled  => OrderState.PartiallyFilled,
        OrderStatus.PartialFill      => OrderState.PartiallyFilled,
        OrderStatus.Fill             => OrderState.Filled,
        OrderStatus.Filled           => OrderState.Filled,
        OrderStatus.Calculated       => OrderState.Filled,
        OrderStatus.DoneForDay       => OrderState.Canceled,
        OrderStatus.Canceled         => OrderState.Canceled,
        OrderStatus.Stopped          => OrderState.Canceled,
        OrderStatus.Replaced         => OrderState.Replaced,
        OrderStatus.PendingCancel    => OrderState.PendingCancel,
        OrderStatus.PendingReplace   => OrderState.PendingReplace,
        OrderStatus.Rejected         => OrderState.Rejected,
        OrderStatus.Suspended        => OrderState.Suspended,
        OrderStatus.Expired          => OrderState.Expired,
        _                            => OrderState.PendingNew,
    };
}
