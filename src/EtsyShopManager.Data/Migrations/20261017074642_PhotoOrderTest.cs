using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotoOrderTest : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoOrderTests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    StepsJson = table.Column<string>(type: "TEXT", nullable: false),
                    ResultJson = table.Column<string>(type: "TEXT", nullable: true),
                    Outcome = table.Column<string>(type: "TEXT", nullable: true),
                    SnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoOrderTests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PhotoOrderTests_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PhotoOrderTests_ListingId",
                table: "PhotoOrderTests",
                column: "ListingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PhotoOrderTests");
        }
    }
}
