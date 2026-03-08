using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlpacaFleece.Infrastructure.Migrations.SignalAttribution
{
    /// <inheritdoc />
    public partial class SignalAttribution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "StrategyName",
                table: "order_intents",
                type: "TEXT",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "StrategyName",
                table: "order_intents");
        }
    }
}
