using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class VariationLists : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "VariationLists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    Options = table.Column<string>(type: "TEXT", nullable: false),
                    NextOptionId = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VariationLists", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListingVariationLists",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    VariationListId = table.Column<int>(type: "INTEGER", nullable: false),
                    PropertyId = table.Column<long>(type: "INTEGER", nullable: false),
                    AppliedOptions = table.Column<string>(type: "TEXT", nullable: false),
                    LinkedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingVariationLists", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingVariationLists_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListingVariationLists_VariationLists_VariationListId",
                        column: x => x.VariationListId,
                        principalTable: "VariationLists",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingVariationLists_ListingId_PropertyId",
                table: "ListingVariationLists",
                columns: new[] { "ListingId", "PropertyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListingVariationLists_VariationListId",
                table: "ListingVariationLists",
                column: "VariationListId");

            migrationBuilder.CreateIndex(
                name: "IX_VariationLists_Name",
                table: "VariationLists",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingVariationLists");

            migrationBuilder.DropTable(
                name: "VariationLists");
        }
    }
}
