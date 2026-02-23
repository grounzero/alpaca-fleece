namespace AlpacaFleece.Infrastructure.Data;

/// <summary>
/// Entity configurations using Fluent API.
/// </summary>

public sealed class OrderIntentEntityConfiguration : IEntityTypeConfiguration<OrderIntentEntity>
{
    public void Configure(EntityTypeBuilder<OrderIntentEntity> builder)
    {
        builder.ToTable("order_intents");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ClientOrderId).HasMaxLength(32).IsRequired();
        builder.HasIndex(x => x.ClientOrderId).IsUnique();
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Side).HasMaxLength(4).IsRequired();
        builder.Property(x => x.LimitPrice).HasPrecision(10, 4);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
    }
}

public sealed class TradeEntityConfiguration : IEntityTypeConfiguration<TradeEntity>
{
    public void Configure(EntityTypeBuilder<TradeEntity> builder)
    {
        builder.ToTable("trades");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.ClientOrderId).HasMaxLength(32).IsRequired();
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.Property(x => x.Side).HasMaxLength(4).IsRequired();
        builder.Property(x => x.AverageEntryPrice).HasPrecision(10, 4);
        builder.Property(x => x.RealizedPnl).HasPrecision(10, 4);
        builder.HasIndex(x => x.Symbol);
    }
}

public sealed class EquityCurveEntityConfiguration : IEntityTypeConfiguration<EquityCurveEntity>
{
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

public sealed class BotStateEntityConfiguration : IEntityTypeConfiguration<BotStateEntity>
{
    public void Configure(EntityTypeBuilder<BotStateEntity> builder)
    {
        builder.ToTable("bot_state");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Key).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Value).HasColumnType("TEXT").IsRequired();
        builder.HasIndex(x => x.Key).IsUnique();
    }
}

public sealed class BarEntityConfiguration : IEntityTypeConfiguration<BarEntity>
{
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

public sealed class PositionSnapshotEntityConfiguration : IEntityTypeConfiguration<PositionSnapshotEntity>
{
    public void Configure(EntityTypeBuilder<PositionSnapshotEntity> builder)
    {
        builder.ToTable("position_snapshots");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.Property(x => x.AverageEntryPrice).HasPrecision(10, 4);
        builder.Property(x => x.CurrentPrice).HasPrecision(10, 4);
        builder.Property(x => x.UnrealizedPnl).HasPrecision(10, 4);
        builder.HasIndex(x => new { x.Symbol, x.SnapshotAt });
    }
}

public sealed class SignalGateEntityConfiguration : IEntityTypeConfiguration<SignalGateEntity>
{
    public void Configure(EntityTypeBuilder<SignalGateEntity> builder)
    {
        builder.ToTable("signal_gates");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.GateName).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => x.GateName).IsUnique();
    }
}

public sealed class FillEntityConfiguration : IEntityTypeConfiguration<FillEntity>
{
    public void Configure(EntityTypeBuilder<FillEntity> builder)
    {
        builder.ToTable("fills");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.AlpacaOrderId).HasMaxLength(50).IsRequired();
        builder.Property(x => x.ClientOrderId).HasMaxLength(32).IsRequired();
        builder.Property(x => x.FilledPrice).HasPrecision(10, 4);
        builder.Property(x => x.FillDedupeKey).HasMaxLength(100).IsRequired();
        builder.HasIndex(x => new { x.AlpacaOrderId, x.FillDedupeKey }).IsUnique();
    }
}

public sealed class PositionTrackingEntityConfiguration : IEntityTypeConfiguration<PositionTrackingEntity>
{
    public void Configure(EntityTypeBuilder<PositionTrackingEntity> builder)
    {
        builder.ToTable("position_tracking");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => x.Symbol).IsUnique();
        builder.Property(x => x.EntryPrice).HasPrecision(10, 4);
        builder.Property(x => x.AtrValue).HasPrecision(10, 4);
        builder.Property(x => x.TrailingStopPrice).HasPrecision(10, 4);
    }
}

public sealed class ExitAttemptEntityConfiguration : IEntityTypeConfiguration<ExitAttemptEntity>
{
    public void Configure(EntityTypeBuilder<ExitAttemptEntity> builder)
    {
        builder.ToTable("exit_attempts");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Symbol).HasMaxLength(10).IsRequired();
        builder.HasIndex(x => x.Symbol).IsUnique();
    }
}

public sealed class ReconciliationReportEntityConfiguration : IEntityTypeConfiguration<ReconciliationReportEntity>
{
    public void Configure(EntityTypeBuilder<ReconciliationReportEntity> builder)
    {
        builder.ToTable("reconciliation_reports");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.TotalPnl).HasPrecision(10, 4);
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.HasIndex(x => x.ReportDate);
    }
}

public sealed class SchemaMetaEntityConfiguration : IEntityTypeConfiguration<SchemaMetaEntity>
{
    public void Configure(EntityTypeBuilder<SchemaMetaEntity> builder)
    {
        builder.ToTable("schema_meta");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Version).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(500);
        builder.HasIndex(x => x.Version).IsUnique();
    }
}

public sealed class CircuitBreakerStateEntityConfiguration : IEntityTypeConfiguration<CircuitBreakerStateEntity>
{
    public void Configure(EntityTypeBuilder<CircuitBreakerStateEntity> builder)
    {
        builder.ToTable("circuit_breaker_state");
        builder.HasKey(x => x.Id);
        builder.HasData(new CircuitBreakerStateEntity { Id = 1, Count = 0, LastResetAt = DateTimeOffset.UtcNow });
    }
}

public sealed class DrawdownStateEntityConfiguration : IEntityTypeConfiguration<DrawdownStateEntity>
{
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
