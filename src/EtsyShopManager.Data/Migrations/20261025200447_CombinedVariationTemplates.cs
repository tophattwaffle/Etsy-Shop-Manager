using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class CombinedVariationTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ListingVariationLists_ListingId_PropertyId",
                table: "ListingVariationLists");

            migrationBuilder.AddColumn<int>(
                name: "Position",
                table: "ListingVariationLists",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_ListingVariationLists_ListingId_PropertyId_VariationListId",
                table: "ListingVariationLists",
                columns: new[] { "ListingId", "PropertyId", "VariationListId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ListingVariationLists_ListingId_PropertyId_VariationListId",
                table: "ListingVariationLists");

            migrationBuilder.DropColumn(
                name: "Position",
                table: "ListingVariationLists");

            migrationBuilder.CreateIndex(
                name: "IX_ListingVariationLists_ListingId_PropertyId",
                table: "ListingVariationLists",
                columns: new[] { "ListingId", "PropertyId" },
                unique: true);
        }
    }
}
