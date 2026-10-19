using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderBuyerProfileName : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BuyerProfileName",
                table: "Orders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BuyerProfileName",
                table: "Orders");
        }
    }
}
