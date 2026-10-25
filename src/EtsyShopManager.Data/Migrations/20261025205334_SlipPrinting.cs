using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class SlipPrinting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "SlipPrintedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SlipQueuedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_SlipQueuedAtUtc",
                table: "Orders",
                column: "SlipQueuedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Orders_SlipQueuedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SlipPrintedAtUtc",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "SlipQueuedAtUtc",
                table: "Orders");
        }
    }
}
