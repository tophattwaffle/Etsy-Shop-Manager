using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class PersonalizationVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PersonalizationCheckAlert",
                table: "Orders",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalizationCheckDismissedHash",
                table: "Orders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalizationCheckHash",
                table: "Orders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PersonalizationCheckStatus",
                table: "Orders",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "PersonalizationCheckedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PersonalizationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false, collation: "NOCASE"),
                    RuleType = table.Column<int>(type: "INTEGER", nullable: false),
                    MinColors = table.Column<int>(type: "INTEGER", nullable: true),
                    MaxColors = table.Column<int>(type: "INTEGER", nullable: true),
                    WordList = table.Column<string>(type: "TEXT", nullable: true),
                    RegexPattern = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    VariationFilter = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CustomAlertMessage = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PersonalizationRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ListingPersonalizationRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    PersonalizationRuleId = table.Column<int>(type: "INTEGER", nullable: false),
                    LinkedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingPersonalizationRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingPersonalizationRules_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ListingPersonalizationRules_PersonalizationRules_PersonalizationRuleId",
                        column: x => x.PersonalizationRuleId,
                        principalTable: "PersonalizationRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingPersonalizationRules_ListingId_PersonalizationRuleId",
                table: "ListingPersonalizationRules",
                columns: new[] { "ListingId", "PersonalizationRuleId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListingPersonalizationRules_PersonalizationRuleId",
                table: "ListingPersonalizationRules",
                column: "PersonalizationRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_PersonalizationRules_Name",
                table: "PersonalizationRules",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingPersonalizationRules");

            migrationBuilder.DropTable(
                name: "PersonalizationRules");

            migrationBuilder.DropColumn(
                name: "PersonalizationCheckAlert",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PersonalizationCheckDismissedHash",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PersonalizationCheckHash",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PersonalizationCheckStatus",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "PersonalizationCheckedAtUtc",
                table: "Orders");
        }
    }
}
