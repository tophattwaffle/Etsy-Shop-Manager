using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListingVideosAndSkuPatterns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SkuPattern",
                table: "Listings",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ListingVideos",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyVideoId = table.Column<long>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: true),
                    Width = table.Column<int>(type: "INTEGER", nullable: true),
                    VideoState = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    SourceUrl = table.Column<string>(type: "TEXT", nullable: false),
                    SourceThumbUrl = table.Column<string>(type: "TEXT", nullable: true),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ThumbFileName = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    ByteSize = table.Column<long>(type: "INTEGER", nullable: true),
                    DownloadedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    DownloadError = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingVideos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingVideos_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingVideos_EtsyVideoId",
                table: "ListingVideos",
                column: "EtsyVideoId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingVideos_FileName",
                table: "ListingVideos",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_ListingVideos_ListingId_EtsyVideoId",
                table: "ListingVideos",
                columns: new[] { "ListingId", "EtsyVideoId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingVideos");

            migrationBuilder.DropColumn(
                name: "SkuPattern",
                table: "Listings");
        }
    }
}
