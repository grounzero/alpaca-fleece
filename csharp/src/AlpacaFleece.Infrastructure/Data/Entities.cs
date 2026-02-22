namespace AlpacaFleece.Infrastructure.Data;

// ============================================================================
// ENTITY CLASSES
// ============================================================================

/// <summary>
/// Order intent (before submission to broker).
/// </summary>
public sealed class OrderIntentEntity
{
    public int Id { get; set; }
    public string ClientOrderId { get; set; } = string.Empty;
    public string? AlpacaOrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal LimitPrice { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}

/// <summary>
/// Completed trade (full fill or partial terminal).
/// </summary>
public sealed class TradeEntity
{
    public int Id { get; set; }
    public string ClientOrderId { get; set; } = string.Empty;
    public string? AlpacaOrderId { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Side { get; set; } = string.Empty;
    public int InitialQuantity { get; set; }
    public int FilledQuantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal RealizedPnl { get; set; }
    public DateTimeOffset EnteredAt { get; set; }
    public DateTimeOffset? ExitedAt { get; set; }
}

/// <summary>
/// Equity curve snapshot (daily closing).
/// </summary>
public sealed class EquityCurveEntity
{
    public int Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public decimal PortfolioValue { get; set; }
    public decimal CashBalance { get; set; }
    public decimal DailyPnl { get; set; }
    public decimal CumulativePnl { get; set; }
}

/// <summary>
/// Key-value store for bot state (like Redis mini).
/// </summary>
public sealed class BotStateEntity
{
    public int Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Bar (OHLCV) snapshots for backtesting / recovery.
/// </summary>
public sealed class BarEntity
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public string Timeframe { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public decimal Open { get; set; }
    public decimal High { get; set; }
    public decimal Low { get; set; }
    public decimal Close { get; set; }
    public long Volume { get; set; }
    public DateTime CreatedAt { get; set; }
}

/// <summary>
/// Position snapshot (point-in-time state).
/// </summary>
public sealed class PositionSnapshotEntity
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public int Quantity { get; set; }
    public decimal AverageEntryPrice { get; set; }
    public decimal CurrentPrice { get; set; }
    public decimal UnrealizedPnl { get; set; }
    public DateTimeOffset SnapshotAt { get; set; }
}

/// <summary>
/// Signal gate tracking (atomic accepts with cooldown).
/// </summary>
public sealed class SignalGateEntity
{
    public int Id { get; set; }
    public string GateName { get; set; } = string.Empty;
    public DateTimeOffset? LastAcceptedBarTs { get; set; }
    public DateTimeOffset? LastAcceptedTs { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// Fill record (order execution partial/full).
/// </summary>
public sealed class FillEntity
{
    public int Id { get; set; }
    public string AlpacaOrderId { get; set; } = string.Empty;
    public string ClientOrderId { get; set; } = string.Empty;
    public int FilledQuantity { get; set; }
    public decimal FilledPrice { get; set; }
    public string FillDedupeKey { get; set; } = string.Empty;
    public DateTimeOffset FilledAt { get; set; }
}

/// <summary>
/// Position tracking (current live positions).
/// </summary>
public sealed class PositionTrackingEntity
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public int CurrentQuantity { get; set; }
    public decimal EntryPrice { get; set; }
    public decimal AtrValue { get; set; }
    public decimal TrailingStopPrice { get; set; }
    public DateTimeOffset LastUpdateAt { get; set; }
}

/// <summary>
/// Exit attempt tracking (for backoff logic).
/// </summary>
public sealed class ExitAttemptEntity
{
    public int Id { get; set; }
    public string Symbol { get; set; } = string.Empty;
    public int AttemptCount { get; set; }
    public DateTimeOffset LastAttemptAt { get; set; }
    public DateTimeOffset NextRetryAt { get; set; }
}

/// <summary>
/// Reconciliation report (nightly audit).
/// </summary>
public sealed class ReconciliationReportEntity
{
    public int Id { get; set; }
    public DateTimeOffset ReportDate { get; set; }
    public int OrdersProcessed { get; set; }
    public int TradesCompleted { get; set; }
    public decimal TotalPnl { get; set; }
    public string Status { get; set; } = string.Empty;
}

/// <summary>
/// Schema version tracking (for migrations).
/// </summary>
public sealed class SchemaMetaEntity
{
    public int Id { get; set; }
    public int Version { get; set; }
    public DateTimeOffset AppliedAt { get; set; }
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Circuit breaker state (kill switch counter).
/// </summary>
public sealed class CircuitBreakerStateEntity
{
    public int Id { get; set; }
    public int Count { get; set; }
    public DateTimeOffset LastResetAt { get; set; }
}
