using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class EditorAndRefill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DraftJson",
                table: "Listings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "DraftUpdatedAtUtc",
                table: "Listings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DraftUpdatedBy",
                table: "Listings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "HasDraft",
                table: "Listings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "HasDrift",
                table: "Listings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "BaseJson",
                table: "ListingEtsyStates",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ListingRefills",
                columns: table => new
                {
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Threshold = table.Column<int>(type: "INTEGER", nullable: false),
                    RefillTo = table.Column<int>(type: "INTEGER", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRefilledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastResult = table.Column<string>(type: "TEXT", nullable: true),
                    Problem = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingRefills", x => x.ListingId);
                    table.ForeignKey(
                        name: "FK_ListingRefills_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ListingSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    TakenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Reason = table.Column<string>(type: "TEXT", nullable: false),
                    ManagedJson = table.Column<string>(type: "TEXT", nullable: false),
                    RawListingJson = table.Column<string>(type: "TEXT", nullable: false),
                    RawInventoryJson = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingSnapshots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingSnapshots_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ListingPushes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RequestedBy = table.Column<string>(type: "TEXT", nullable: true),
                    RequestedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ChangesJson = table.Column<string>(type: "TEXT", nullable: false),
                    Error = table.Column<string>(type: "TEXT", nullable: true),
                    SnapshotId = table.Column<int>(type: "INTEGER", nullable: true),
                    JobId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingPushes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingPushes_ListingSnapshots_SnapshotId",
                        column: x => x.SnapshotId,
                        principalTable: "ListingSnapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ListingPushes_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_HasDraft",
                table: "Listings",
                column: "HasDraft");

            migrationBuilder.CreateIndex(
                name: "IX_Listings_HasDrift",
                table: "Listings",
                column: "HasDrift");

            migrationBuilder.CreateIndex(
                name: "IX_ListingPushes_ListingId_RequestedAtUtc",
                table: "ListingPushes",
                columns: new[] { "ListingId", "RequestedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingPushes_SnapshotId",
                table: "ListingPushes",
                column: "SnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingRefills_Enabled",
                table: "ListingRefills",
                column: "Enabled");

            migrationBuilder.CreateIndex(
                name: "IX_ListingSnapshots_ListingId_TakenAtUtc",
                table: "ListingSnapshots",
                columns: new[] { "ListingId", "TakenAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingPushes");

            migrationBuilder.DropTable(
                name: "ListingRefills");

            migrationBuilder.DropTable(
                name: "ListingSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_Listings_HasDraft",
                table: "Listings");

            migrationBuilder.DropIndex(
                name: "IX_Listings_HasDrift",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "DraftJson",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "DraftUpdatedAtUtc",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "DraftUpdatedBy",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "HasDraft",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "HasDrift",
                table: "Listings");

            migrationBuilder.DropColumn(
                name: "BaseJson",
                table: "ListingEtsyStates");
        }
    }
}
