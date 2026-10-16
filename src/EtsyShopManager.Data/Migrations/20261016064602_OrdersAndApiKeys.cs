using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrdersAndApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ApiKeys",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Prefix = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Hash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreatedBy = table.Column<string>(type: "TEXT", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RevokedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ApiKeys", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Orders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyReceiptId = table.Column<long>(type: "INTEGER", nullable: false),
                    BuyerUserId = table.Column<long>(type: "INTEGER", nullable: false),
                    BuyerName = table.Column<string>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    IsPaid = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsShipped = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsGift = table.Column<bool>(type: "INTEGER", nullable: false),
                    GiftMessage = table.Column<string>(type: "TEXT", nullable: true),
                    MessageFromBuyer = table.Column<string>(type: "TEXT", nullable: true),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Divisor = table.Column<int>(type: "INTEGER", nullable: false),
                    Subtotal = table.Column<long>(type: "INTEGER", nullable: true),
                    Grandtotal = table.Column<long>(type: "INTEGER", nullable: true),
                    ShippingCost = table.Column<long>(type: "INTEGER", nullable: true),
                    TaxCost = table.Column<long>(type: "INTEGER", nullable: true),
                    DiscountAmount = table.Column<long>(type: "INTEGER", nullable: true),
                    EtsyCreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EtsyUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ShipByUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    FirstSeenAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Orders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Orders_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopOrderSyncStates",
                columns: table => new
                {
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    ModifiedSinceUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Offset = table.Column<int>(type: "INTEGER", nullable: false),
                    PollStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    BackfillCompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastPolledAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopOrderSyncStates", x => x.ShopId);
                    table.ForeignKey(
                        name: "FK_ShopOrderSyncStates_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    OrderId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyTransactionId = table.Column<long>(type: "INTEGER", nullable: false),
                    EtsyListingId = table.Column<long>(type: "INTEGER", nullable: true),
                    EtsyProductId = table.Column<long>(type: "INTEGER", nullable: true),
                    ListingImageId = table.Column<long>(type: "INTEGER", nullable: true),
                    Title = table.Column<string>(type: "TEXT", nullable: true),
                    Sku = table.Column<string>(type: "TEXT", nullable: true),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    PriceAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceDivisor = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", nullable: true),
                    Variations = table.Column<string>(type: "TEXT", nullable: false),
                    ExpectedShipAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EtsyCreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ImageUrl = table.Column<string>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderItems_Orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "Orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Hash",
                table: "ApiKeys",
                column: "Hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_EtsyListingId",
                table: "OrderItems",
                column: "EtsyListingId");

            migrationBuilder.CreateIndex(
                name: "IX_OrderItems_OrderId_EtsyTransactionId",
                table: "OrderItems",
                columns: new[] { "OrderId", "EtsyTransactionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_EtsyCreatedAtUtc",
                table: "Orders",
                column: "EtsyCreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Orders_EtsyReceiptId",
                table: "Orders",
                column: "EtsyReceiptId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Orders_ShopId_IsPaid_IsShipped",
                table: "Orders",
                columns: new[] { "ShopId", "IsPaid", "IsShipped" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ApiKeys");

            migrationBuilder.DropTable(
                name: "OrderItems");

            migrationBuilder.DropTable(
                name: "ShopOrderSyncStates");

            migrationBuilder.DropTable(
                name: "Orders");
        }
    }
}
