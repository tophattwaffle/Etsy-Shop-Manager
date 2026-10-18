using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class PhotoTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PhotoTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false, collation: "NOCASE"),
                    FileName = table.Column<string>(type: "TEXT", nullable: false),
                    AltText = table.Column<string>(type: "TEXT", nullable: false),
                    RankFromEnd = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PhotoTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListingPhotoTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    PhotoTemplateId = table.Column<int>(type: "INTEGER", nullable: false),
                    AppliedFileName = table.Column<string>(type: "TEXT", nullable: true),
                    AppliedAltText = table.Column<string>(type: "TEXT", nullable: true),
                    PendingFileName = table.Column<string>(type: "TEXT", nullable: true),
                    LinkedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingPhotoTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingPhotoTemplates_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListingPhotoTemplates_PhotoTemplates_PhotoTemplateId",
                        column: x => x.PhotoTemplateId,
                        principalTable: "PhotoTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingPhotoTemplates_ListingId_PhotoTemplateId",
                table: "ListingPhotoTemplates",
                columns: new[] { "ListingId", "PhotoTemplateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListingPhotoTemplates_PhotoTemplateId",
                table: "ListingPhotoTemplates",
                column: "PhotoTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_PhotoTemplates_AltText",
                table: "PhotoTemplates",
                column: "AltText",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PhotoTemplates_Name",
                table: "PhotoTemplates",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingPhotoTemplates");

            migrationBuilder.DropTable(
                name: "PhotoTemplates");
        }
    }
}
