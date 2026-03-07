using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AlpacaFleece.Infrastructure.Migrations.OrderStateTimestamps
{
    /// <inheritdoc />
    public partial class AddOrderIntentsStatusIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_order_intents_Status",
                table: "order_intents",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_order_intents_Symbol_Side_Status",
                table: "order_intents",
                columns: new[] { "Symbol", "Side", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_order_intents_Status",
                table: "order_intents");

            migrationBuilder.DropIndex(
                name: "IX_order_intents_Symbol_Side_Status",
                table: "order_intents");
        }
    }
}
