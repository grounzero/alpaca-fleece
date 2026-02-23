namespace AlpacaFleece.Core.Interfaces;

/// <summary>
/// Repository for state persistence (SQLite).
/// Owns all queries; DDL managed by SchemaManager.
/// </summary>
public interface IStateRepository
{
    /// <summary>
    /// Gets a key-value pair from bot_state table.
    /// </summary>
    ValueTask<string?> GetStateAsync(string key, CancellationToken ct = default);

    /// <summary>
    /// Sets a key-value pair in bot_state table (upsert).
    /// </summary>
    ValueTask SetStateAsync(string key, string value, CancellationToken ct = default);

    /// <summary>
    /// Atomically checks and accepts a gate (signal gate or similar).
    /// Returns true if accepted (monotonic update), false if rejected.
    /// Uses Serializable isolation for atomicity.
    /// </summary>
    ValueTask<bool> GateTryAcceptAsync(
        string gateName,
        DateTimeOffset barTimestamp,
        DateTimeOffset nowUtc,
        TimeSpan cooldown,
        CancellationToken ct = default);

    /// <summary>
    /// Saves an order intent to persistence (for crash recovery).
    /// </summary>
    ValueTask SaveOrderIntentAsync(
        string clientOrderId,
        string symbol,
        string side,
        int quantity,
        decimal limitPrice,
        DateTimeOffset createdAt,
        CancellationToken ct = default);

    /// <summary>
    /// Updates an existing order intent status.
    /// </summary>
    ValueTask UpdateOrderIntentAsync(
        string clientOrderId,
        string alpacaOrderId,
        OrderState status,
        DateTimeOffset updatedAt,
        CancellationToken ct = default);

    /// <summary>
    /// Retrieves an order intent by client order ID.
    /// </summary>
    ValueTask<OrderIntentDto?> GetOrderIntentAsync(string clientOrderId, CancellationToken ct = default);

    /// <summary>
    /// Idempotently inserts a fill, using alpaca_order_id + fill_dedupe_key as unique constraint.
    /// </summary>
    ValueTask InsertFillIdempotentAsync(
        string alpacaOrderId,
        string clientOrderId,
        int filledQuantity,
        decimal filledPrice,
        string dedupeKey, // timestamp or sequence-based
        DateTimeOffset filledAt,
        CancellationToken ct = default);

    /// <summary>
    /// Gets exponential backoff seconds for exit attempts on a symbol.
    /// </summary>
    ValueTask<int> GetExitBackoffSecondsAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Gets current circuit breaker count.
    /// </summary>
    ValueTask<int> GetCircuitBreakerCountAsync(CancellationToken ct = default);

    /// <summary>
    /// Increments circuit breaker count.
    /// </summary>
    ValueTask SaveCircuitBreakerCountAsync(int count, CancellationToken ct = default);

    /// <summary>
    /// Resets daily state (circuit breaker, trade count, etc.).
    /// </summary>
    ValueTask ResetDailyStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Records an exit attempt for backoff tracking.
    /// </summary>
    ValueTask RecordExitAttemptAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Records a failed exit attempt, incrementing attempt count.
    /// </summary>
    ValueTask RecordExitAttemptFailureAsync(string symbol, CancellationToken ct = default);

    /// <summary>
    /// Inserts an equity snapshot to equity_curve table.
    /// </summary>
    ValueTask InsertEquitySnapshotAsync(
        DateTimeOffset timestamp,
        decimal portfolioValue,
        decimal cashBalance,
        decimal dailyPnl,
        CancellationToken ct = default);

    /// <summary>
    /// Inserts a reconciliation report to reconciliation_reports table.
    /// </summary>
    ValueTask InsertReconciliationReportAsync(
        string reportJson,
        CancellationToken ct = default);

    /// <summary>
    /// Gets all order intents from the database.
    /// </summary>
    ValueTask<IReadOnlyList<OrderIntentDto>> GetAllOrderIntentsAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets all position tracking records from the database.
    /// </summary>
    ValueTask<IReadOnlyList<(string Symbol, int Quantity, decimal EntryPrice, decimal AtrValue)>>
        GetAllPositionTrackingAsync(CancellationToken ct = default);

    /// <summary>
    /// Gets the persisted drawdown state (peak equity, current drawdown, level).
    /// Returns null if no state has been saved yet.
    /// </summary>
    ValueTask<DrawdownStateDto?> GetDrawdownStateAsync(CancellationToken ct = default);

    /// <summary>
    /// Persists the current drawdown state (upsert, single row).
    /// </summary>
    ValueTask SaveDrawdownStateAsync(
        DrawdownLevel level,
        decimal peakEquity,
        decimal drawdownPct,
        DateTimeOffset lastPeakResetTime,
        bool manualRecoveryRequested,
        CancellationToken ct = default);
}

/// <summary>
/// DTO for drawdown state from database.
/// </summary>
public record DrawdownStateDto(
    DrawdownLevel Level,
    decimal PeakEquity,
    decimal CurrentDrawdownPct,
    DateTimeOffset LastUpdated,
    DateTimeOffset LastPeakResetTime,
    bool ManualRecoveryRequested);

/// <summary>
/// DTO for order intent from database.
/// </summary>
public record OrderIntentDto(
    string ClientOrderId,
    string? AlpacaOrderId,
    string Symbol,
    string Side,
    int Quantity,
    decimal LimitPrice,
    OrderState Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);
