using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlpacaFleece.Infrastructure.Migrations.FractionalQuantity
{
    /// <inheritdoc />
    public partial class FractionalQuantity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<decimal>(
                name: "InitialQuantity",
                table: "trades",
                type: "TEXT",
                precision: 18,
                scale: 8,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<decimal>(
                name: "FilledQuantity",
                table: "trades",
                type: "TEXT",
                precision: 18,
                scale: 8,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<decimal>(
                name: "CurrentQuantity",
                table: "position_tracking",
                type: "TEXT",
                precision: 18,
                scale: 8,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "position_snapshots",
                type: "TEXT",
                precision: 18,
                scale: 8,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "order_intents",
                type: "TEXT",
                precision: 18,
                scale: 8,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");

            migrationBuilder.AlterColumn<decimal>(
                name: "FilledQuantity",
                table: "fills",
                type: "TEXT",
                precision: 18,
                scale: 8,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "INTEGER");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<int>(
                name: "InitialQuantity",
                table: "trades",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldPrecision: 18,
                oldScale: 8);

            migrationBuilder.AlterColumn<int>(
                name: "FilledQuantity",
                table: "trades",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldPrecision: 18,
                oldScale: 8);

            migrationBuilder.AlterColumn<int>(
                name: "CurrentQuantity",
                table: "position_tracking",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldPrecision: 18,
                oldScale: 8);

            migrationBuilder.AlterColumn<int>(
                name: "Quantity",
                table: "position_snapshots",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldPrecision: 18,
                oldScale: 8);

            migrationBuilder.AlterColumn<int>(
                name: "Quantity",
                table: "order_intents",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldPrecision: 18,
                oldScale: 8);

            migrationBuilder.AlterColumn<int>(
                name: "FilledQuantity",
                table: "fills",
                type: "INTEGER",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "TEXT",
                oldPrecision: 18,
                oldScale: 8);
        }
    }
}
