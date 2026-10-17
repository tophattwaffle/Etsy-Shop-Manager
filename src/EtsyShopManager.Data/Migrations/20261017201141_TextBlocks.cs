using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class TextBlocks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TextBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Text = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TextBlocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListingTextBlocks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    TextBlockId = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedText = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingTextBlocks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingTextBlocks_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListingTextBlocks_TextBlocks_TextBlockId",
                        column: x => x.TextBlockId,
                        principalTable: "TextBlocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingTextBlocks_ListingId_TextBlockId",
                table: "ListingTextBlocks",
                columns: new[] { "ListingId", "TextBlockId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListingTextBlocks_TextBlockId",
                table: "ListingTextBlocks",
                column: "TextBlockId");

            migrationBuilder.CreateIndex(
                name: "IX_TextBlocks_Name",
                table: "TextBlocks",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingTextBlocks");

            migrationBuilder.DropTable(
                name: "TextBlocks");
        }
    }
}
