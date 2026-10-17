using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListingClone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ListingClones",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    SourceListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    TargetShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SettingsJson = table.Column<string>(type: "TEXT", nullable: false),
                    NewEtsyListingId = table.Column<long>(type: "INTEGER", nullable: true),
                    NewListingId = table.Column<int>(type: "INTEGER", nullable: true),
                    Log = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingClones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingClones_Listings_SourceListingId",
                        column: x => x.SourceListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListingClones_Shops_TargetShopId",
                        column: x => x.TargetShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingClones_SourceListingId",
                table: "ListingClones",
                column: "SourceListingId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingClones_TargetShopId",
                table: "ListingClones",
                column: "TargetShopId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingClones");
        }
    }
}
