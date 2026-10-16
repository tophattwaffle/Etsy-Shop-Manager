using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListingImagesUniquePerListing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ListingImages_EtsyImageId",
                table: "ListingImages");

            migrationBuilder.CreateIndex(
                name: "IX_ListingImages_EtsyImageId",
                table: "ListingImages",
                column: "EtsyImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingImages_ListingId_EtsyImageId",
                table: "ListingImages",
                columns: new[] { "ListingId", "EtsyImageId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ListingImages_EtsyImageId",
                table: "ListingImages");

            migrationBuilder.DropIndex(
                name: "IX_ListingImages_ListingId_EtsyImageId",
                table: "ListingImages");

            migrationBuilder.CreateIndex(
                name: "IX_ListingImages_EtsyImageId",
                table: "ListingImages",
                column: "EtsyImageId",
                unique: true);
        }
    }
}
