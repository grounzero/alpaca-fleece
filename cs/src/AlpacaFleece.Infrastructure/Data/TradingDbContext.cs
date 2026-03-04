namespace AlpacaFleece.Infrastructure.Data;

/// <summary>
/// Entity Framework Core DbContext for trading system.
/// All entity mappings via IEntityTypeConfiguration classes (FLUENT API).
/// </summary>
public sealed class TradingDbContext(DbContextOptions<TradingDbContext> options) : DbContext(options)
{
    /// <summary>
    /// Gets or sets the DbSet for OrderIntentEntity.
    /// </summary>
    public DbSet<OrderIntentEntity> OrderIntents { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for TradeEntity.
    /// </summary>
    public DbSet<TradeEntity> Trades { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for EquityCurveEntity.
    /// </summary>
    public DbSet<EquityCurveEntity> EquityCurve { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for BotStateEntity.
    /// </summary>
    public DbSet<BotStateEntity> BotState { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for BarEntity.
    /// </summary>
    public DbSet<BarEntity> Bars { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for PositionSnapshotEntity.
    /// </summary>
    public DbSet<PositionSnapshotEntity> PositionSnapshots { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for SignalGateEntity.
    /// </summary>
    public DbSet<SignalGateEntity> SignalGates { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for FillEntity.
    /// </summary>
    public DbSet<FillEntity> Fills { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for PositionTrackingEntity.
    /// </summary>
    public DbSet<PositionTrackingEntity> PositionTracking { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for ExitAttemptEntity.
    /// </summary>
    public DbSet<ExitAttemptEntity> ExitAttempts { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for ReconciliationReportEntity.
    /// </summary>
    public DbSet<ReconciliationReportEntity> ReconciliationReports { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for SchemaMetaEntity.
    /// </summary>
    public DbSet<SchemaMetaEntity> SchemaMeta { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for CircuitBreakerStateEntity.
    /// </summary>
    public DbSet<CircuitBreakerStateEntity> CircuitBreakerState { get; set; } = null!;

    /// <summary>
    /// Gets or sets the DbSet for DrawdownStateEntity.
    /// </summary>
    public DbSet<DrawdownStateEntity> DrawdownState { get; set; } = null!;

    /// <summary>
    /// Configures the model builder with all entity type configurations.
    /// Applies fluent API mappings for each entity using IEntityTypeConfiguration implementations.
    /// </summary>
    /// <param name="modelBuilder">The EF Core model builder.</param>
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
        modelBuilder.ApplyConfiguration(new DrawdownStateEntityConfiguration());
    }
}
