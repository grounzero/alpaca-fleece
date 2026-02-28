using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlpacaFleece.Infrastructure.Migrations.InitialCreate
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "bars",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Timeframe = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Open = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    High = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    Low = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    Close = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    Volume = table.Column<long>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bars", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "bot_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Key = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bot_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "circuit_breaker_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Count = table.Column<int>(type: "INTEGER", nullable: false),
                    LastResetAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_circuit_breaker_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "drawdown_state",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Level = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PeakEquity = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    CurrentDrawdownPct = table.Column<decimal>(type: "TEXT", precision: 10, scale: 6, nullable: false),
                    LastUpdated = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastPeakResetTime = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ManualRecoveryRequested = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_drawdown_state", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "equity_curve",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Timestamp = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    PortfolioValue = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    CashBalance = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    DailyPnl = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    CumulativePnl = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_equity_curve", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "exit_attempts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastAttemptAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    NextRetryAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exit_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "fills",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AlpacaOrderId = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    ClientOrderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    FilledQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    FilledPrice = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    FillDedupeKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    FilledAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fills", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "order_intents",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientOrderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AlpacaOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    LimitPrice = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_intents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "position_snapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageEntryPrice = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    CurrentPrice = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    UnrealizedPnl = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    SnapshotAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_snapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "position_tracking",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    CurrentQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    EntryPrice = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    AtrValue = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    TrailingStopPrice = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    LastUpdateAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_tracking", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "reconciliation_reports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ReportDate = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    OrdersProcessed = table.Column<int>(type: "INTEGER", nullable: false),
                    TradesCompleted = table.Column<int>(type: "INTEGER", nullable: false),
                    TotalPnl = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reconciliation_reports", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "schema_meta",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Version = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Description = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schema_meta", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "signal_gates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    GateName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    LastAcceptedBarTs = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    LastAcceptedTs = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_gates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "trades",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ClientOrderId = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    AlpacaOrderId = table.Column<string>(type: "TEXT", nullable: true),
                    Symbol = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Side = table.Column<string>(type: "TEXT", maxLength: 4, nullable: false),
                    InitialQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    FilledQuantity = table.Column<int>(type: "INTEGER", nullable: false),
                    AverageEntryPrice = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    RealizedPnl = table.Column<decimal>(type: "TEXT", precision: 10, scale: 4, nullable: false),
                    EnteredAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    ExitedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_trades", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "circuit_breaker_state",
                columns: new[] { "Id", "Count", "LastResetAt" },
                values: new object[] { 1, 0, new DateTimeOffset(new DateTime(2026, 2, 28, 19, 7, 12, 733, DateTimeKind.Unspecified).AddTicks(3940), new TimeSpan(0, 0, 0, 0, 0)) });

            migrationBuilder.InsertData(
                table: "drawdown_state",
                columns: new[] { "Id", "CurrentDrawdownPct", "LastPeakResetTime", "LastUpdated", "Level", "ManualRecoveryRequested", "PeakEquity" },
                values: new object[] { 1, 0m, new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new DateTimeOffset(new DateTime(2024, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Normal", false, 0m });

            migrationBuilder.CreateIndex(
                name: "IX_bars_Symbol_Timeframe_Timestamp",
                table: "bars",
                columns: new[] { "Symbol", "Timeframe", "Timestamp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bot_state_Key",
                table: "bot_state",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_equity_curve_Timestamp",
                table: "equity_curve",
                column: "Timestamp",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exit_attempts_Symbol",
                table: "exit_attempts",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fills_AlpacaOrderId_FillDedupeKey",
                table: "fills",
                columns: new[] { "AlpacaOrderId", "FillDedupeKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_ClientOrderId",
                table: "order_intents",
                column: "ClientOrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_position_snapshots_Symbol_SnapshotAt",
                table: "position_snapshots",
                columns: new[] { "Symbol", "SnapshotAt" });

            migrationBuilder.CreateIndex(
                name: "IX_position_tracking_Symbol",
                table: "position_tracking",
                column: "Symbol",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_reconciliation_reports_ReportDate",
                table: "reconciliation_reports",
                column: "ReportDate");

            migrationBuilder.CreateIndex(
                name: "IX_schema_meta_Version",
                table: "schema_meta",
                column: "Version",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signal_gates_GateName",
                table: "signal_gates",
                column: "GateName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_trades_Symbol",
                table: "trades",
                column: "Symbol");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "bars");

            migrationBuilder.DropTable(
                name: "bot_state");

            migrationBuilder.DropTable(
                name: "circuit_breaker_state");

            migrationBuilder.DropTable(
                name: "drawdown_state");

            migrationBuilder.DropTable(
                name: "equity_curve");

            migrationBuilder.DropTable(
                name: "exit_attempts");

            migrationBuilder.DropTable(
                name: "fills");

            migrationBuilder.DropTable(
                name: "order_intents");

            migrationBuilder.DropTable(
                name: "position_snapshots");

            migrationBuilder.DropTable(
                name: "position_tracking");

            migrationBuilder.DropTable(
                name: "reconciliation_reports");

            migrationBuilder.DropTable(
                name: "schema_meta");

            migrationBuilder.DropTable(
                name: "signal_gates");

            migrationBuilder.DropTable(
                name: "trades");
        }
    }
}
