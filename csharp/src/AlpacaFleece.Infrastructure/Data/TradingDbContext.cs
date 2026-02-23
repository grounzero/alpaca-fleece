namespace AlpacaFleece.Infrastructure.Data;

/// <summary>
/// Entity Framework Core DbContext for trading system.
/// All entity mappings via IEntityTypeConfiguration classes (FLUENT API).
/// </summary>
public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    public DbSet<OrderIntentEntity> OrderIntents { get; set; } = null!;
    public DbSet<TradeEntity> Trades { get; set; } = null!;
    public DbSet<EquityCurveEntity> EquityCurve { get; set; } = null!;
    public DbSet<BotStateEntity> BotState { get; set; } = null!;
    public DbSet<BarEntity> Bars { get; set; } = null!;
    public DbSet<PositionSnapshotEntity> PositionSnapshots { get; set; } = null!;
    public DbSet<SignalGateEntity> SignalGates { get; set; } = null!;
    public DbSet<FillEntity> Fills { get; set; } = null!;
    public DbSet<PositionTrackingEntity> PositionTracking { get; set; } = null!;
    public DbSet<ExitAttemptEntity> ExitAttempts { get; set; } = null!;
    public DbSet<ReconciliationReportEntity> ReconciliationReports { get; set; } = null!;
    public DbSet<SchemaMetaEntity> SchemaMeta { get; set; } = null!;
    public DbSet<CircuitBreakerStateEntity> CircuitBreakerState { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new OrderIntentEntityConfiguration());
        modelBuilder.ApplyConfiguration(new TradeEntityConfiguration());
        modelBuilder.ApplyConfiguration(new EquityCurveEntityConfiguration());
        modelBuilder.ApplyConfiguration(new BotStateEntityConfiguration());
        modelBuilder.ApplyConfiguration(new BarEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PositionSnapshotEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SignalGateEntityConfiguration());
        modelBuilder.ApplyConfiguration(new FillEntityConfiguration());
        modelBuilder.ApplyConfiguration(new PositionTrackingEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ExitAttemptEntityConfiguration());
        modelBuilder.ApplyConfiguration(new ReconciliationReportEntityConfiguration());
        modelBuilder.ApplyConfiguration(new SchemaMetaEntityConfiguration());
        modelBuilder.ApplyConfiguration(new CircuitBreakerStateEntityConfiguration());
    }
}
