namespace AlpacaFleece.Infrastructure.Data;

// ============================================================================
// ENTITY CLASSES
// ============================================================================

/// <summary>
/// Order intent entity (before submission to broker).
/// Persists trading order details including client-assigned and broker-assigned order IDs,
/// symbol, side, quantity, and price. Tracks state transitions with nullable timestamps.
/// </summary>
/// <example>
/// <code>
/// var intent = new OrderIntentEntity
/// {
///     ClientOrderId = "client-123",
///     Symbol = "AAPL",
///     Side = "buy",
///     Quantity = 100m,
///     LimitPrice = 150.00m,
///     Status = "submitted",
///     CreatedAt = DateTimeOffset.UtcNow
/// };
/// </code>
/// </example>
public sealed class OrderIntentEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the client-generated order ID (deterministic, for idempotency).
    /// </summary>
    public string ClientOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the broker-assigned order ID (assigned after submission).
    /// </summary>
    public string? AlpacaOrderId { get; set; }

    /// <summary>
    /// Gets or sets the trading symbol (e.g., AAPL).
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the order side (buy or sell).
    /// </summary>
    public string Side { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the order quantity.
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Gets or sets the limit price (0 for market orders).
    /// </summary>
    public decimal LimitPrice { get; set; }

    /// <summary>
    /// Gets or sets the current order status (e.g., submitted, accepted, filled).
    /// </summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp when the order intent was created.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last update.
    /// </summary>
    public DateTimeOffset? UpdatedAt { get; set; }

    /// <summary>
    /// Gets or sets the ATR seed value used for position sizing.
    /// </summary>
    public decimal? AtrSeed { get; set; }

    // O-2: State transition timestamps — preserved on first transition (??= idiom in StateRepository).
    /// <summary>
    /// Gets or sets the timestamp when the order was accepted by the broker (first transition only).
    /// </summary>
    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the order was fully filled (first transition only).
    /// </summary>
    public DateTimeOffset? FilledAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the order was cancelled (first transition only).
    /// </summary>
    public DateTimeOffset? CanceledAt { get; set; }
}

/// <summary>
/// Completed trade entity (full fill or partial terminal state).
/// Persists trade execution details after order completion.
/// </summary>
/// <example>
/// <code>
/// var trade = new TradeEntity
/// {
///     ClientOrderId = "client-123",
///     Symbol = "AAPL",
///     Side = "buy",
///     FilledQuantity = 100m,
///     AverageEntryPrice = 150.25m,
///     EnteredAt = DateTimeOffset.UtcNow
/// };
/// </code>
/// </example>
public sealed class TradeEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the client-generated order ID.
    /// </summary>
    public string ClientOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the broker-assigned order ID.
    /// </summary>
    public string? AlpacaOrderId { get; set; }

    /// <summary>
    /// Gets or sets the trading symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the trade side (buy or sell).
    /// </summary>
    public string Side { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the initial quantity requested.
    /// </summary>
    public decimal InitialQuantity { get; set; }

    /// <summary>
    /// Gets or sets the actual filled quantity.
    /// </summary>
    public decimal FilledQuantity { get; set; }

    /// <summary>
    /// Gets or sets the average execution price of filled shares.
    /// </summary>
    public decimal AverageEntryPrice { get; set; }

    /// <summary>
    /// Gets or sets the realised profit/loss for the trade.
    /// </summary>
    public decimal RealizedPnl { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the position was entered.
    /// </summary>
    public DateTimeOffset EnteredAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the position was exited (if completed).
    /// </summary>
    public DateTimeOffset? ExitedAt { get; set; }
}

/// <summary>
/// Equity curve snapshot entity (daily closing).
/// Records portfolio value, cash balance, and profit/loss metrics at discrete points in time.
/// </summary>
public sealed class EquityCurveEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the snapshot.
    /// </summary>
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the total portfolio value.
    /// </summary>
    public decimal PortfolioValue { get; set; }

    /// <summary>
    /// Gets or sets the cash balance.
    /// </summary>
    public decimal CashBalance { get; set; }

    /// <summary>
    /// Gets or sets the daily profit/loss.
    /// </summary>
    public decimal DailyPnl { get; set; }

    /// <summary>
    /// Gets or sets the cumulative profit/loss across all trades.
    /// </summary>
    public decimal CumulativePnl { get; set; }
}

/// <summary>
/// Key-value store entity for bot state (like Redis).
/// Used for persistent state tracking across restarts.
/// </summary>
public sealed class BotStateEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the state key.
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the state value.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp of the last update.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Bar (OHLCV) entity for backtesting and recovery.
/// Stores candlestick data with open, high, low, close, and volume.
/// </summary>
public sealed class BarEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the trading symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timeframe (e.g., 1min, 5min, 1hour).
    /// </summary>
    public string Timeframe { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the bar timestamp.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Gets or sets the opening price.
    /// </summary>
    public decimal Open { get; set; }

    /// <summary>
    /// Gets or sets the highest price in the bar.
    /// </summary>
    public decimal High { get; set; }

    /// <summary>
    /// Gets or sets the lowest price in the bar.
    /// </summary>
    public decimal Low { get; set; }

    /// <summary>
    /// Gets or sets the closing price.
    /// </summary>
    public decimal Close { get; set; }

    /// <summary>
    /// Gets or sets the trading volume.
    /// </summary>
    public long Volume { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the bar was recorded.
    /// </summary>
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Position snapshot entity (point-in-time state).
/// Records position details at a specific moment for analysis and reconciliation.
/// </summary>
public sealed class PositionSnapshotEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the trading symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the quantity held.
    /// </summary>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Gets or sets the average entry price.
    /// </summary>
    public decimal AverageEntryPrice { get; set; }

    /// <summary>
    /// Gets or sets the current price at snapshot time.
    /// </summary>
    public decimal CurrentPrice { get; set; }

    /// <summary>
    /// Gets or sets the unrealised profit/loss.
    /// </summary>
    public decimal UnrealizedPnl { get; set; }

    /// <summary>
    /// Gets or sets the snapshot timestamp.
    /// </summary>
    public DateTimeOffset SnapshotAt { get; set; }
}

/// <summary>
/// Signal gate entity (atomic accepts with cooldown).
/// Tracks the last accepted signal timestamp for deduplication and rate-limiting.
/// </summary>
public sealed class SignalGateEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the gate name (unique identifier for the signal gate).
    /// </summary>
    public string GateName { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the timestamp of the last accepted bar.
    /// </summary>
    public DateTimeOffset? LastAcceptedBarTs { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last accepted signal.
    /// </summary>
    public DateTimeOffset? LastAcceptedTs { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last update.
    /// </summary>
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Fill record entity (order execution partial/full).
/// Records individual execution fills with deduplication support.
/// </summary>
public sealed class FillEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the broker-assigned order ID.
    /// </summary>
    public string AlpacaOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client-generated order ID.
    /// </summary>
    public string ClientOrderId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the filled quantity.
    /// </summary>
    public decimal FilledQuantity { get; set; }

    /// <summary>
    /// Gets or sets the filled price.
    /// </summary>
    public decimal FilledPrice { get; set; }

    /// <summary>
    /// Gets or sets the fill deduplication key.
    /// </summary>
    public string FillDedupeKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the fill timestamp.
    /// </summary>
    public DateTimeOffset FilledAt { get; set; }
}

/// <summary>
/// Position tracking entity (current live positions).
/// Stores live position details for in-memory and persistent state synchronisation.
/// </summary>
public sealed class PositionTrackingEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the trading symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the current quantity held.
    /// </summary>
    public decimal CurrentQuantity { get; set; }

    /// <summary>
    /// Gets or sets the entry price.
    /// </summary>
    public decimal EntryPrice { get; set; }

    /// <summary>
    /// Gets or sets the Average True Range value.
    /// </summary>
    public decimal AtrValue { get; set; }

    /// <summary>
    /// Gets or sets the trailing stop price.
    /// </summary>
    public decimal TrailingStopPrice { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last update.
    /// </summary>
    public DateTimeOffset LastUpdateAt { get; set; }
}

/// <summary>
/// Exit attempt tracking entity (for backoff logic).
/// Tracks failed exit attempts and retry scheduling per symbol.
/// </summary>
public sealed class ExitAttemptEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the trading symbol.
    /// </summary>
    public string Symbol { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the count of exit attempts.
    /// </summary>
    public int AttemptCount { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last attempt.
    /// </summary>
    public DateTimeOffset LastAttemptAt { get; set; }

    /// <summary>
    /// Gets or sets the timestamp scheduled for the next retry.
    /// </summary>
    public DateTimeOffset NextRetryAt { get; set; }
}

/// <summary>
/// Reconciliation report entity (nightly audit).
/// Stores daily reconciliation results between local state and broker records.
/// </summary>
public sealed class ReconciliationReportEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the date of the report.
    /// </summary>
    public DateTimeOffset ReportDate { get; set; }

    /// <summary>
    /// Gets or sets the count of orders processed.
    /// </summary>
    public int OrdersProcessed { get; set; }

    /// <summary>
    /// Gets or sets the count of trades completed.
    /// </summary>
    public int TradesCompleted { get; set; }

    /// <summary>
    /// Gets or sets the total profit/loss for the report period.
    /// </summary>
    public decimal TotalPnl { get; set; }

    /// <summary>
    /// Gets or sets the report status.
    /// </summary>
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Schema version tracking entity (for migrations).
/// Maintains a record of all applied schema versions for migration management.
/// </summary>
public sealed class SchemaMetaEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the schema version number.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Gets or sets the timestamp when the schema version was applied.
    /// </summary>
    public DateTimeOffset AppliedAt { get; set; }

    /// <summary>
    /// Gets or sets a descriptive text about the schema change.
    /// </summary>
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Circuit breaker state entity (kill switch counter).
/// Tracks the number of failures and last reset time for circuit breaker functionality.
/// </summary>
public sealed class CircuitBreakerStateEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the failure count.
    /// </summary>
    public int Count { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last counter reset.
    /// </summary>
    public DateTimeOffset LastResetAt { get; set; }
}

/// <summary>
/// Drawdown state entity (single row; tracks peak equity, escalation level, and recovery settings).
/// Maintains the current drawdown monitoring state across restarts.
/// </summary>
public sealed class DrawdownStateEntity
{
    /// <summary>
    /// Gets or sets the primary key identifier.
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// Gets or sets the current drawdown level (Normal, Warning, Halt, or Emergency).
    /// </summary>
    public string Level { get; set; } = "Normal";

    /// <summary>
    /// Gets or sets the peak equity value used for drawdown calculations.
    /// </summary>
    public decimal PeakEquity { get; set; }

    /// <summary>
    /// Gets or sets the current drawdown percentage.
    /// </summary>
    public decimal CurrentDrawdownPct { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last update.
    /// </summary>
    public DateTimeOffset LastUpdated { get; set; }

    /// <summary>
    /// Gets or sets the timestamp of the last peak reset (for rolling lookback window).
    /// </summary>
    public DateTimeOffset LastPeakResetTime { get; set; }

    /// <summary>
    /// Gets or sets a flag indicating whether manual recovery has been requested.
    /// Only applies when auto-recovery is disabled. Cleared after startup when recovery is validated.
    /// </summary>
    public bool ManualRecoveryRequested { get; set; }
}
