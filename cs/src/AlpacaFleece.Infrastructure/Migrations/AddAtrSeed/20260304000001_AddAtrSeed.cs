using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlpacaFleece.Infrastructure.Migrations.AddAtrSeed
{
    /// <inheritdoc />
    public partial class AddAtrSeed : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AtrSeed",
                table: "order_intents",
                type: "TEXT",
                precision: 10,
                scale: 4,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AtrSeed",
                table: "order_intents");
        }
    }
}
