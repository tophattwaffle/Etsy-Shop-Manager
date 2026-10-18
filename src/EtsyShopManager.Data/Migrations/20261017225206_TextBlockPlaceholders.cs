using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class TextBlockPlaceholders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Slug",
                table: "TextBlocks",
                type: "TEXT",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "ListingDescriptions",
                columns: table => new
                {
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Source = table.Column<string>(type: "TEXT", nullable: false),
                    Rendered = table.Column<string>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingDescriptions", x => x.ListingId);
                    table.ForeignKey(
                        name: "FK_ListingDescriptions_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Existing blocks get a unique placeholder slug ("~id", not a valid slug); at startup the app gives each one a
            // slug made from its name (TextBlockService.ConvertLinksAsync).
            migrationBuilder.Sql("UPDATE TextBlocks SET Slug = '~' || Id;");

            migrationBuilder.CreateIndex(
                name: "IX_TextBlocks_Slug",
                table: "TextBlocks",
                column: "Slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingDescriptions");

            migrationBuilder.DropIndex(
                name: "IX_TextBlocks_Slug",
                table: "TextBlocks");

            migrationBuilder.DropColumn(
                name: "Slug",
                table: "TextBlocks");
        }
    }
}
