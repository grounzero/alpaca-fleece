namespace AlpacaFleece.Infrastructure.Data;

/// <summary>
/// Entity configurations using Fluent API.
/// </summary>

public sealed class OrderIntentEntityConfiguration : IEntityTypeConfiguration<OrderIntentEntity>
{
    /// <summary>
    /// Configures the OrderIntentEntity mapping using EF Core Fluent API.
    /// Maps to 'order_intents' table with constraints for order ID, symbol, and state transitions.
    /// </summary>
    /// <param name="builder">The entity type builder for OrderIntentEntity configuration.</param>
    public void Configure(EntityTypeBuilder<OrderIntentEntity> builder)
    {
        builder.ToTable("order_intents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ClientOrderId).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.ClientOrderId).IsUnique();
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Side).HasMaxLength(4).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 8);
        builder.Property(x => x.LimitPrice).HasPrecision(10, 4);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.AtrSeed).HasPrecision(10, 4);
        // O-2: Order state transition timestamps (nullable — set once on first transition).
        builder.Property(x => x.AcceptedAt).HasColumnName("accepted_at").IsRequired(false);
        builder.Property(x => x.FilledAt).HasColumnName("filled_at").IsRequired(false);
        builder.Property(x => x.CanceledAt).HasColumnName("canceled_at").IsRequired(false);
    }
}

/// <summary>
/// Entity configuration for TradeEntity using EF Core Fluent API.
/// </summary>
public sealed class TradeEntityConfiguration : IEntityTypeConfiguration<TradeEntity>
{
    /// <summary>
    /// Configures the TradeEntity mapping to 'trades' table with required properties and indexes.
    /// </summary>
    /// <param name="builder">The entity type builder for TradeEntity configuration.</param>
    public void Configure(EntityTypeBuilder<TradeEntity> builder)
    {
        builder.ToTable("trades");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ClientOrderId).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Side).HasMaxLength(4).IsRequired();
        builder.Property(x => x.InitialQuantity).HasPrecision(18, 8);
        builder.Property(x => x.FilledQuantity).HasPrecision(18, 8);
        builder.Property(x => x.AverageEntryPrice).HasPrecision(10, 4);
        builder.Property(x => x.RealizedPnl).HasPrecision(10, 4);
        builder.HasIndex(x => x.Symbol);
    }
}

/// <summary>
/// Entity configuration for EquityCurveEntity using EF Core Fluent API.
/// </summary>
public sealed class EquityCurveEntityConfiguration : IEntityTypeConfiguration<EquityCurveEntity>
{
    /// <summary>
    /// Configures the EquityCurveEntity mapping to 'equity_curve' table with unique timestamp index.
    /// </summary>
    /// <param name="builder">The entity type builder for EquityCurveEntity configuration.</param>
    public void Configure(EntityTypeBuilder<EquityCurveEntity> builder)
    {
        builder.ToTable("equity_curve");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.PortfolioValue).HasPrecision(10, 4);
        builder.Property(x => x.CashBalance).HasPrecision(10, 4);
        builder.Property(x => x.DailyPnl).HasPrecision(10, 4);
        builder.Property(x => x.CumulativePnl).HasPrecision(10, 4);
        builder.HasIndex(x => x.Timestamp).IsUnique();
    }
}

/// <summary>
/// Entity configuration for BotStateEntity using EF Core Fluent API.
/// </summary>
public sealed class BotStateEntityConfiguration : IEntityTypeConfiguration<BotStateEntity>
{
    /// <summary>
    /// Configures the BotStateEntity mapping to 'bot_state' table with unique key constraint.
    /// </summary>
    /// <param name="builder">The entity type builder for BotStateEntity configuration.</param>
    public void Configure(EntityTypeBuilder<BotStateEntity> builder)
    {
        builder.ToTable("bot_state");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Value).HasColumnType("TEXT").IsRequired();
        builder.HasIndex(x => x.Key).IsUnique();
    }
}

/// <summary>
/// Entity configuration for BarEntity using EF Core Fluent API.
/// </summary>
public sealed class BarEntityConfiguration : IEntityTypeConfiguration<BarEntity>
{
    /// <summary>
    /// Configures the BarEntity mapping to 'bars' table with composite index on symbol, timeframe, and timestamp.
    /// </summary>
    /// <param name="builder">The entity type builder for BarEntity configuration.</param>
    public void Configure(EntityTypeBuilder<BarEntity> builder)
    {
        builder.ToTable("bars");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Timeframe).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Open).HasPrecision(10, 4);
        builder.Property(x => x.High).HasPrecision(10, 4);
        builder.Property(x => x.Low).HasPrecision(10, 4);
        builder.Property(x => x.Close).HasPrecision(10, 4);
        builder.HasIndex(x => new { x.Symbol, x.Timeframe, x.Timestamp }).IsUnique();
    }
}

/// <summary>
/// Entity configuration for PositionSnapshotEntity using EF Core Fluent API.
/// </summary>
public sealed class PositionSnapshotEntityConfiguration : IEntityTypeConfiguration<PositionSnapshotEntity>
{
    /// <summary>
    /// Configures the PositionSnapshotEntity mapping to 'position_snapshots' table with composite index.
    /// </summary>
    /// <param name="builder">The entity type builder for PositionSnapshotEntity configuration.</param>
    public void Configure(EntityTypeBuilder<PositionSnapshotEntity> builder)
    {
        builder.ToTable("position_snapshots");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Quantity).HasPrecision(18, 8);
        builder.Property(x => x.AverageEntryPrice).HasPrecision(10, 4);
        builder.Property(x => x.CurrentPrice).HasPrecision(10, 4);
        builder.Property(x => x.UnrealizedPnl).HasPrecision(10, 4);
        builder.HasIndex(x => new { x.Symbol, x.SnapshotAt });
    }
}

/// <summary>
/// Entity configuration for SignalGateEntity using EF Core Fluent API.
/// </summary>
public sealed class SignalGateEntityConfiguration : IEntityTypeConfiguration<SignalGateEntity>
{
    /// <summary>
    /// Configures the SignalGateEntity mapping to 'signal_gates' table with unique gate name index.
    /// </summary>
    /// <param name="builder">The entity type builder for SignalGateEntity configuration.</param>
    public void Configure(EntityTypeBuilder<SignalGateEntity> builder)
    {
        builder.ToTable("signal_gates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.GateName).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.GateName).IsUnique();
    }
}

/// <summary>
/// Entity configuration for FillEntity using EF Core Fluent API.
/// </summary>
public sealed class FillEntityConfiguration : IEntityTypeConfiguration<FillEntity>
{
    /// <summary>
    /// Configures the FillEntity mapping to 'fills' table with deduplication index.
    /// </summary>
    /// <param name="builder">The entity type builder for FillEntity configuration.</param>
    public void Configure(EntityTypeBuilder<FillEntity> builder)
    {
        builder.ToTable("fills");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AlpacaOrderId).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ClientOrderId).HasMaxLength(32).IsRequired();
        builder.Property(x => x.FilledQuantity).HasPrecision(18, 8);
        builder.Property(x => x.FilledPrice).HasPrecision(10, 4);
        builder.Property(x => x.FillDedupeKey).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => new { x.AlpacaOrderId, x.FillDedupeKey }).IsUnique();
    }
}

/// <summary>
/// Entity configuration for PositionTrackingEntity using EF Core Fluent API.
/// </summary>
public sealed class PositionTrackingEntityConfiguration : IEntityTypeConfiguration<PositionTrackingEntity>
{
    /// <summary>
    /// Configures the PositionTrackingEntity mapping to 'position_tracking' table with unique symbol index.
    /// </summary>
    /// <param name="builder">The entity type builder for PositionTrackingEntity configuration.</param>
    public void Configure(EntityTypeBuilder<PositionTrackingEntity> builder)
    {
        builder.ToTable("position_tracking");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => x.Symbol).IsUnique();
        builder.Property(x => x.CurrentQuantity).HasPrecision(18, 8);
        builder.Property(x => x.EntryPrice).HasPrecision(10, 4);
        builder.Property(x => x.AtrValue).HasPrecision(10, 4);
        builder.Property(x => x.TrailingStopPrice).HasPrecision(10, 4);
    }
}

/// <summary>
/// Entity configuration for ExitAttemptEntity using EF Core Fluent API.
/// </summary>
public sealed class ExitAttemptEntityConfiguration : IEntityTypeConfiguration<ExitAttemptEntity>
{
    /// <summary>
    /// Configures the ExitAttemptEntity mapping to 'exit_attempts' table with unique symbol index.
    /// </summary>
    /// <param name="builder">The entity type builder for ExitAttemptEntity configuration.</param>
    public void Configure(EntityTypeBuilder<ExitAttemptEntity> builder)
    {
        builder.ToTable("exit_attempts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => x.Symbol).IsUnique();
    }
}

/// <summary>
/// Entity configuration for ReconciliationReportEntity using EF Core Fluent API.
/// </summary>
public sealed class ReconciliationReportEntityConfiguration : IEntityTypeConfiguration<ReconciliationReportEntity>
{
    /// <summary>
    /// Configures the ReconciliationReportEntity mapping to 'reconciliation_reports' table with date index.
    /// </summary>
    /// <param name="builder">The entity type builder for ReconciliationReportEntity configuration.</param>
    public void Configure(EntityTypeBuilder<ReconciliationReportEntity> builder)
    {
        builder.ToTable("reconciliation_reports");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TotalPnl).HasPrecision(10, 4);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.HasIndex(x => x.ReportDate);
    }
}

/// <summary>
/// Entity configuration for SchemaMetaEntity using EF Core Fluent API.
/// </summary>
public sealed class SchemaMetaEntityConfiguration : IEntityTypeConfiguration<SchemaMetaEntity>
{
    /// <summary>
    /// Configures the SchemaMetaEntity mapping to 'schema_meta' table with unique version constraint.
    /// </summary>
    /// <param name="builder">The entity type builder for SchemaMetaEntity configuration.</param>
    public void Configure(EntityTypeBuilder<SchemaMetaEntity> builder)
    {
        builder.ToTable("schema_meta");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.HasIndex(x => x.Version).IsUnique();
    }
}

/// <summary>
/// Entity configuration for CircuitBreakerStateEntity using EF Core Fluent API.
/// </summary>
public sealed class CircuitBreakerStateEntityConfiguration : IEntityTypeConfiguration<CircuitBreakerStateEntity>
{
    /// <summary>
    /// Configures the CircuitBreakerStateEntity mapping to 'circuit_breaker_state' table with initial seed data.
    /// </summary>
    /// <param name="builder">The entity type builder for CircuitBreakerStateEntity configuration.</param>
    public void Configure(EntityTypeBuilder<CircuitBreakerStateEntity> builder)
    {
        builder.ToTable("circuit_breaker_state");
        builder.HasKey(x => x.Id);
        // Seed with a fixed timestamp so the EF Core model is deterministic (matches generated migration snapshot).
        builder.HasData(new CircuitBreakerStateEntity
        {
            Id = 1,
            Count = 0,
            LastResetAt = DateTimeOffset.UnixEpoch
        });
    }
}

/// <summary>
/// Entity configuration for DrawdownStateEntity using EF Core Fluent API.
/// </summary>
public sealed class DrawdownStateEntityConfiguration : IEntityTypeConfiguration<DrawdownStateEntity>
{
    /// <summary>
    /// Configures the DrawdownStateEntity mapping to 'drawdown_state' table with initial seed data.
    /// </summary>
    /// <param name="builder">The entity type builder for DrawdownStateEntity configuration.</param>
    public void Configure(EntityTypeBuilder<DrawdownStateEntity> builder)
    {
        builder.ToTable("drawdown_state");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Level).HasMaxLength(20).IsRequired();
        builder.Property(x => x.PeakEquity).HasPrecision(10, 4);
        builder.Property(x => x.CurrentDrawdownPct).HasPrecision(10, 6);
        builder.HasData(new DrawdownStateEntity
        {
            Id = 1,
            Level = "Normal",
            PeakEquity = 0m,
            CurrentDrawdownPct = 0m,
            LastUpdated = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            LastPeakResetTime = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            ManualRecoveryRequested = false
        });
    }
}
