using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderAddressVerification : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AddressCheckAlert",
                table: "Orders",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCheckDismissedHash",
                table: "Orders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCheckHash",
                table: "Orders",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCheckRawReply",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCheckStatus",
                table: "Orders",
                type: "TEXT",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AddressCheckSuggested",
                table: "Orders",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "AddressCheckedAtUtc",
                table: "Orders",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AddressCheckAlert",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AddressCheckDismissedHash",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AddressCheckHash",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AddressCheckRawReply",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AddressCheckStatus",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AddressCheckSuggested",
                table: "Orders");

            migrationBuilder.DropColumn(
                name: "AddressCheckedAtUtc",
                table: "Orders");
        }
    }
}
