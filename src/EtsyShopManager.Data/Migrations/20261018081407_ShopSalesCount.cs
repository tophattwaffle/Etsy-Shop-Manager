using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ShopSalesCount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TransactionSoldCount",
                table: "Shops",
                type: "INTEGER",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransactionSoldCount",
                table: "Shops");
        }
    }
}
