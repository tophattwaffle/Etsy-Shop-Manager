using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EtsyShopManager.Data.Migrations
{
    /// <inheritdoc />
    public partial class ListingsImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Listings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyListingId = table.Column<long>(type: "INTEGER", nullable: false),
                    State = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Title = table.Column<string>(type: "TEXT", maxLength: 400, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    PriceAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceDivisor = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", maxLength: 8, nullable: true),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    Skus = table.Column<string>(type: "TEXT", nullable: false),
                    Tags = table.Column<string>(type: "TEXT", nullable: false),
                    Materials = table.Column<string>(type: "TEXT", nullable: false),
                    Styles = table.Column<string>(type: "TEXT", nullable: false),
                    TaxonomyId = table.Column<long>(type: "INTEGER", nullable: true),
                    ShippingProfileId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReturnPolicyId = table.Column<long>(type: "INTEGER", nullable: true),
                    ReadinessStateId = table.Column<long>(type: "INTEGER", nullable: true),
                    ShopSectionId = table.Column<long>(type: "INTEGER", nullable: true),
                    FeaturedRank = table.Column<int>(type: "INTEGER", nullable: true),
                    WhoMade = table.Column<string>(type: "TEXT", nullable: true),
                    WhenMade = table.Column<string>(type: "TEXT", nullable: true),
                    IsSupply = table.Column<bool>(type: "INTEGER", nullable: true),
                    ListingType = table.Column<string>(type: "TEXT", nullable: true),
                    IsCustomizable = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPersonalizable = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsTaxable = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShouldAutoRenew = table.Column<bool>(type: "INTEGER", nullable: false),
                    HasVariations = table.Column<bool>(type: "INTEGER", nullable: false),
                    ProcessingMin = table.Column<int>(type: "INTEGER", nullable: true),
                    ProcessingMax = table.Column<int>(type: "INTEGER", nullable: true),
                    ItemWeight = table.Column<double>(type: "REAL", nullable: true),
                    ItemWeightUnit = table.Column<string>(type: "TEXT", nullable: true),
                    ItemLength = table.Column<double>(type: "REAL", nullable: true),
                    ItemWidth = table.Column<double>(type: "REAL", nullable: true),
                    ItemHeight = table.Column<double>(type: "REAL", nullable: true),
                    ItemDimensionsUnit = table.Column<string>(type: "TEXT", nullable: true),
                    ProductionPartnerIds = table.Column<string>(type: "TEXT", nullable: false),
                    PersonalizationJson = table.Column<string>(type: "TEXT", nullable: true),
                    Attributes = table.Column<string>(type: "TEXT", nullable: false),
                    PriceOnProperty = table.Column<string>(type: "TEXT", nullable: false),
                    QuantityOnProperty = table.Column<string>(type: "TEXT", nullable: false),
                    SkuOnProperty = table.Column<string>(type: "TEXT", nullable: false),
                    ReadinessStateOnProperty = table.Column<string>(type: "TEXT", nullable: false),
                    Url = table.Column<string>(type: "TEXT", nullable: true),
                    Views = table.Column<int>(type: "INTEGER", nullable: true),
                    NumFavorers = table.Column<int>(type: "INTEGER", nullable: true),
                    EtsyCreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EtsyUpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EtsyStateChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    EtsyEndsAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    FirstImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    LastImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AttributesFetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastSeenRunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    IsMissingOnEtsy = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Listings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Listings_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    ReadinessStateId = table.Column<long>(type: "INTEGER", nullable: false),
                    ReadinessState = table.Column<string>(type: "TEXT", nullable: false),
                    MinProcessingDays = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxProcessingDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DisplayLabel = table.Column<string>(type: "TEXT", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProcessingProfiles_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ProductionPartners",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyPartnerId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    Location = table.Column<string>(type: "TEXT", nullable: true),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductionPartners", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductionPartners_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReturnPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyReturnPolicyId = table.Column<long>(type: "INTEGER", nullable: false),
                    AcceptsReturns = table.Column<bool>(type: "INTEGER", nullable: false),
                    AcceptsExchanges = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReturnDeadlineDays = table.Column<int>(type: "INTEGER", nullable: true),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReturnPolicies_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShippingProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyShippingProfileId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    OriginCountryIso = table.Column<string>(type: "TEXT", nullable: true),
                    OriginPostalCode = table.Column<string>(type: "TEXT", nullable: true),
                    ProfileType = table.Column<string>(type: "TEXT", nullable: true),
                    DestinationCount = table.Column<int>(type: "INTEGER", nullable: false),
                    UpgradeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShippingProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShippingProfiles_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopSections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsySectionId = table.Column<long>(type: "INTEGER", nullable: false),
                    Title = table.Column<string>(type: "TEXT", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    ActiveListingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopSections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ShopSections_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ShopSyncStates",
                columns: table => new
                {
                    ShopId = table.Column<int>(type: "INTEGER", nullable: false),
                    RunId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    RunStartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    RunForce = table.Column<bool>(type: "INTEGER", nullable: false),
                    CursorJson = table.Column<string>(type: "TEXT", nullable: true),
                    LastCompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LookupsImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastListingCount = table.Column<int>(type: "INTEGER", nullable: true),
                    LastError = table.Column<string>(type: "TEXT", nullable: true),
                    LastErrorAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ShopSyncStates", x => x.ShopId);
                    table.ForeignKey(
                        name: "FK_ShopSyncStates_Shops_ShopId",
                        column: x => x.ShopId,
                        principalTable: "Shops",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TaxonomyNodes",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false),
                    ParentId = table.Column<long>(type: "INTEGER", nullable: true),
                    Level = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    FullPath = table.Column<string>(type: "TEXT", nullable: false),
                    HasChildren = table.Column<bool>(type: "INTEGER", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxonomyNodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TaxonomyProperties",
                columns: table => new
                {
                    TaxonomyId = table.Column<long>(type: "INTEGER", nullable: false),
                    PropertyId = table.Column<long>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", nullable: true),
                    IsRequired = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportsAttributes = table.Column<bool>(type: "INTEGER", nullable: false),
                    SupportsVariations = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsMultivalued = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxValuesAllowed = table.Column<int>(type: "INTEGER", nullable: true),
                    ScalesJson = table.Column<string>(type: "TEXT", nullable: false),
                    PossibleValuesJson = table.Column<string>(type: "TEXT", nullable: false),
                    PossibleValueCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ImportedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaxonomyProperties", x => new { x.TaxonomyId, x.PropertyId });
                });

            migrationBuilder.CreateTable(
                name: "ListingEtsyStates",
                columns: table => new
                {
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    RawJson = table.Column<string>(type: "TEXT", nullable: false),
                    ManagedJson = table.Column<string>(type: "TEXT", nullable: false),
                    ManagedHash = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    ManagedVersion = table.Column<int>(type: "INTEGER", nullable: false),
                    FetchedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    ManagedChangedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingEtsyStates", x => x.ListingId);
                    table.ForeignKey(
                        name: "FK_ListingEtsyStates_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ListingImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyImageId = table.Column<long>(type: "INTEGER", nullable: false),
                    Rank = table.Column<int>(type: "INTEGER", nullable: false),
                    AltText = table.Column<string>(type: "TEXT", nullable: true),
                    FullWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    FullHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    HexCode = table.Column<string>(type: "TEXT", nullable: true),
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
                    table.PrimaryKey("PK_ListingImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingImages_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ListingProducts",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ListingId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyProductId = table.Column<long>(type: "INTEGER", nullable: false),
                    Sku = table.Column<string>(type: "TEXT", nullable: true),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    PropertyValues = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingProducts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingProducts_Listings_ListingId",
                        column: x => x.ListingId,
                        principalTable: "Listings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ListingOfferings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ProductId = table.Column<int>(type: "INTEGER", nullable: false),
                    EtsyOfferingId = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceAmount = table.Column<long>(type: "INTEGER", nullable: false),
                    PriceDivisor = table.Column<int>(type: "INTEGER", nullable: false),
                    CurrencyCode = table.Column<string>(type: "TEXT", nullable: true),
                    Quantity = table.Column<int>(type: "INTEGER", nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsDeleted = table.Column<bool>(type: "INTEGER", nullable: false),
                    ReadinessStateId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ListingOfferings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ListingOfferings_ListingProducts_ProductId",
                        column: x => x.ProductId,
                        principalTable: "ListingProducts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ListingImages_EtsyImageId",
                table: "ListingImages",
                column: "EtsyImageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ListingImages_FileName",
                table: "ListingImages",
                column: "FileName");

            migrationBuilder.CreateIndex(
                name: "IX_ListingImages_ListingId_Rank",
                table: "ListingImages",
                columns: new[] { "ListingId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_ListingImages_ThumbFileName",
                table: "ListingImages",
                column: "ThumbFileName");

            migrationBuilder.CreateIndex(
                name: "IX_ListingOfferings_ProductId",
                table: "ListingOfferings",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingProducts_ListingId",
                table: "ListingProducts",
                column: "ListingId");

            migrationBuilder.CreateIndex(
                name: "IX_ListingProducts_Sku",
                table: "ListingProducts",
                column: "Sku");

            migrationBuilder.CreateIndex(
                name: "IX_Listings_EtsyListingId",
                table: "Listings",
                column: "EtsyListingId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Listings_EtsyUpdatedAtUtc",
                table: "Listings",
                column: "EtsyUpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Listings_ShopId_State",
                table: "Listings",
                columns: new[] { "ShopId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_Listings_Title",
                table: "Listings",
                column: "Title");

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingProfiles_ShopId_ReadinessStateId",
                table: "ProcessingProfiles",
                columns: new[] { "ShopId", "ReadinessStateId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProductionPartners_ShopId_EtsyPartnerId",
                table: "ProductionPartners",
                columns: new[] { "ShopId", "EtsyPartnerId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReturnPolicies_ShopId_EtsyReturnPolicyId",
                table: "ReturnPolicies",
                columns: new[] { "ShopId", "EtsyReturnPolicyId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShippingProfiles_ShopId_EtsyShippingProfileId",
                table: "ShippingProfiles",
                columns: new[] { "ShopId", "EtsyShippingProfileId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShopSections_ShopId_EtsySectionId",
                table: "ShopSections",
                columns: new[] { "ShopId", "EtsySectionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaxonomyNodes_ParentId",
                table: "TaxonomyNodes",
                column: "ParentId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ListingEtsyStates");

            migrationBuilder.DropTable(
                name: "ListingImages");

            migrationBuilder.DropTable(
                name: "ListingOfferings");

            migrationBuilder.DropTable(
                name: "ProcessingProfiles");

            migrationBuilder.DropTable(
                name: "ProductionPartners");

            migrationBuilder.DropTable(
                name: "ReturnPolicies");

            migrationBuilder.DropTable(
                name: "ShippingProfiles");

            migrationBuilder.DropTable(
                name: "ShopSections");

            migrationBuilder.DropTable(
                name: "ShopSyncStates");

            migrationBuilder.DropTable(
                name: "TaxonomyNodes");

            migrationBuilder.DropTable(
                name: "TaxonomyProperties");

            migrationBuilder.DropTable(
                name: "ListingProducts");

            migrationBuilder.DropTable(
                name: "Listings");
        }
    }
}
