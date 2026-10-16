using System.Text.Json;
using System.Text.Json.Serialization;

namespace EtsyShopManager.Etsy.Models;

// Hand-written from Etsy's OpenAPI spec (https://www.etsy.com/openapi/generated/oas/3.0.0.json), covering the
// fields the app uses. Unknown fields are ignored, and the raw JSON is kept wherever the app stores Etsy state.

/// <summary>Etsy's standard collection envelope: { "count": n, "results": [...] }.</summary>
public sealed class EtsyPage<T>
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("results")] public List<T> Results { get; set; } = [];
}

/// <summary>A deserialized item plus the exact JSON Etsy sent for it.</summary>
public sealed record EtsyRaw<T>(T Value, string Json);

public sealed record EtsyRawPage<T>(int Count, List<EtsyRaw<T>> Results);

public sealed class EtsyMoney
{
    [JsonPropertyName("amount")] public long Amount { get; set; }
    [JsonPropertyName("divisor")] public int Divisor { get; set; } = 100;
    [JsonPropertyName("currency_code")] public string? CurrencyCode { get; set; }
}

/// <summary>Listing states accepted by getListingsByShop.</summary>
public static class EtsyListingStates
{
    public const string Active = "active";
    public const string Inactive = "inactive";
    public const string SoldOut = "sold_out";
    public const string Draft = "draft";
    public const string Expired = "expired";

    /// <summary>Every state a seller can still see. ("removed" listings are gone for good.)</summary>
    public static readonly string[] All = [Active, Inactive, SoldOut, Draft, Expired];
}

/// <summary>ShopListingWithAssociations.</summary>
public sealed class EtsyListing
{
    [JsonPropertyName("listing_id")] public long ListingId { get; set; }
    [JsonPropertyName("user_id")] public long UserId { get; set; }
    [JsonPropertyName("shop_id")] public long ShopId { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("created_timestamp")] public long? CreatedTimestamp { get; set; }
    [JsonPropertyName("original_creation_timestamp")] public long? OriginalCreationTimestamp { get; set; }
    [JsonPropertyName("ending_timestamp")] public long? EndingTimestamp { get; set; }
    [JsonPropertyName("last_modified_timestamp")] public long? LastModifiedTimestamp { get; set; }
    [JsonPropertyName("updated_timestamp")] public long? UpdatedTimestamp { get; set; }
    [JsonPropertyName("state_timestamp")] public long? StateTimestamp { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("shop_section_id")] public long? ShopSectionId { get; set; }
    [JsonPropertyName("featured_rank")] public int? FeaturedRank { get; set; }
    [JsonPropertyName("url")] public string? Url { get; set; }
    [JsonPropertyName("num_favorers")] public int? NumFavorers { get; set; }
    [JsonPropertyName("is_taxable")] public bool IsTaxable { get; set; }
    [JsonPropertyName("is_customizable")] public bool IsCustomizable { get; set; }
    [JsonPropertyName("is_personalizable")] public bool IsPersonalizable { get; set; }
    [JsonPropertyName("listing_type")] public string? ListingType { get; set; }
    [JsonPropertyName("tags")] public List<string>? Tags { get; set; }
    [JsonPropertyName("materials")] public List<string>? Materials { get; set; }
    [JsonPropertyName("style")] public List<string>? Style { get; set; }
    [JsonPropertyName("shipping_profile_id")] public long? ShippingProfileId { get; set; }
    [JsonPropertyName("return_policy_id")] public long? ReturnPolicyId { get; set; }
    [JsonPropertyName("processing_min")] public int? ProcessingMin { get; set; }
    [JsonPropertyName("processing_max")] public int? ProcessingMax { get; set; }
    [JsonPropertyName("who_made")] public string? WhoMade { get; set; }
    [JsonPropertyName("when_made")] public string? WhenMade { get; set; }
    [JsonPropertyName("is_supply")] public bool? IsSupply { get; set; }
    [JsonPropertyName("item_weight")] public decimal? ItemWeight { get; set; }
    [JsonPropertyName("item_weight_unit")] public string? ItemWeightUnit { get; set; }
    [JsonPropertyName("item_length")] public decimal? ItemLength { get; set; }
    [JsonPropertyName("item_width")] public decimal? ItemWidth { get; set; }
    [JsonPropertyName("item_height")] public decimal? ItemHeight { get; set; }
    [JsonPropertyName("item_dimensions_unit")] public string? ItemDimensionsUnit { get; set; }
    [JsonPropertyName("has_variations")] public bool HasVariations { get; set; }
    [JsonPropertyName("should_auto_renew")] public bool ShouldAutoRenew { get; set; }
    [JsonPropertyName("price")] public EtsyMoney? Price { get; set; }
    [JsonPropertyName("taxonomy_id")] public long? TaxonomyId { get; set; }
    [JsonPropertyName("readiness_state_id")] public long? ReadinessStateId { get; set; }
    [JsonPropertyName("skus")] public List<string>? Skus { get; set; }
    [JsonPropertyName("views")] public int? Views { get; set; }
    [JsonPropertyName("production_partners")] public List<EtsyProductionPartner>? ProductionPartners { get; set; }

    // Includes (Images, Inventory, Personalization). Null when not requested or not returned.
    [JsonPropertyName("images")] public List<EtsyListingImage>? Images { get; set; }
    [JsonPropertyName("inventory")] public EtsyInventory? Inventory { get; set; }
    [JsonPropertyName("personalization")] public JsonElement? Personalization { get; set; }
}

public sealed class EtsyListingImage
{
    [JsonPropertyName("listing_id")] public long ListingId { get; set; }
    [JsonPropertyName("listing_image_id")] public long ListingImageId { get; set; }
    [JsonPropertyName("hex_code")] public string? HexCode { get; set; }
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("url_75x75")] public string? Url75x75 { get; set; }
    [JsonPropertyName("url_170x135")] public string? Url170x135 { get; set; }
    [JsonPropertyName("url_570xN")] public string? Url570xN { get; set; }
    [JsonPropertyName("url_fullxfull")] public string? UrlFullxFull { get; set; }
    [JsonPropertyName("full_height")] public int? FullHeight { get; set; }
    [JsonPropertyName("full_width")] public int? FullWidth { get; set; }
    [JsonPropertyName("alt_text")] public string? AltText { get; set; }
}

public sealed class EtsyInventory
{
    [JsonPropertyName("products")] public List<EtsyProduct> Products { get; set; } = [];
    [JsonPropertyName("price_on_property")] public List<long>? PriceOnProperty { get; set; }
    [JsonPropertyName("quantity_on_property")] public List<long>? QuantityOnProperty { get; set; }
    [JsonPropertyName("sku_on_property")] public List<long>? SkuOnProperty { get; set; }
    [JsonPropertyName("readiness_state_on_property")] public List<long>? ReadinessStateOnProperty { get; set; }

    /// <summary>Only with includes=Listing on getListingInventory.</summary>
    [JsonPropertyName("listing")] public EtsyListing? Listing { get; set; }
}

public sealed class EtsyProduct
{
    [JsonPropertyName("product_id")] public long ProductId { get; set; }
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("offerings")] public List<EtsyOffering> Offerings { get; set; } = [];
    [JsonPropertyName("property_values")] public List<EtsyPropertyValue> PropertyValues { get; set; } = [];
}

public sealed class EtsyOffering
{
    [JsonPropertyName("offering_id")] public long OfferingId { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("is_enabled")] public bool IsEnabled { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("price")] public EtsyMoney? Price { get; set; }
    [JsonPropertyName("readiness_state_id")] public long? ReadinessStateId { get; set; }
}

/// <summary>ListingPropertyValue (inventory property values and listing attributes).</summary>
public sealed class EtsyPropertyValue
{
    [JsonPropertyName("property_id")] public long PropertyId { get; set; }
    [JsonPropertyName("property_name")] public string? PropertyName { get; set; }
    [JsonPropertyName("scale_id")] public long? ScaleId { get; set; }
    [JsonPropertyName("scale_name")] public string? ScaleName { get; set; }
    [JsonPropertyName("value_ids")] public List<long>? ValueIds { get; set; }
    [JsonPropertyName("values")] public List<string>? Values { get; set; }
}

public sealed class EtsyProductionPartner
{
    [JsonPropertyName("production_partner_id")] public long ProductionPartnerId { get; set; }
    [JsonPropertyName("partner_name")] public string? PartnerName { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
}

public sealed class EtsyShippingProfile
{
    [JsonPropertyName("shipping_profile_id")] public long ShippingProfileId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("origin_country_iso")] public string? OriginCountryIso { get; set; }
    [JsonPropertyName("origin_postal_code")] public string? OriginPostalCode { get; set; }
    [JsonPropertyName("profile_type")] public string? ProfileType { get; set; }
    [JsonPropertyName("is_deleted")] public bool IsDeleted { get; set; }
    [JsonPropertyName("shipping_profile_destinations")] public List<JsonElement>? Destinations { get; set; }
    [JsonPropertyName("shipping_profile_upgrades")] public List<JsonElement>? Upgrades { get; set; }
}

/// <summary>ShopProcessingProfile (readiness state definition).</summary>
public sealed class EtsyProcessingProfile
{
    [JsonPropertyName("readiness_state_id")] public long ReadinessStateId { get; set; }
    [JsonPropertyName("readiness_state")] public string? ReadinessState { get; set; }
    [JsonPropertyName("min_processing_days")] public int MinProcessingDays { get; set; }
    [JsonPropertyName("max_processing_days")] public int MaxProcessingDays { get; set; }
    [JsonPropertyName("processing_days_display_label")] public string? ProcessingDaysDisplayLabel { get; set; }
}

public sealed class EtsyReturnPolicy
{
    [JsonPropertyName("return_policy_id")] public long ReturnPolicyId { get; set; }
    [JsonPropertyName("accepts_returns")] public bool AcceptsReturns { get; set; }
    [JsonPropertyName("accepts_exchanges")] public bool AcceptsExchanges { get; set; }
    [JsonPropertyName("return_deadline")] public int? ReturnDeadline { get; set; }
}

public sealed class EtsyShopSection
{
    [JsonPropertyName("shop_section_id")] public long ShopSectionId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("rank")] public int Rank { get; set; }
    [JsonPropertyName("active_listing_count")] public int ActiveListingCount { get; set; }
}

public sealed class EtsyTaxonomyNode
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("parent_id")] public long? ParentId { get; set; }
    [JsonPropertyName("children")] public List<EtsyTaxonomyNode>? Children { get; set; }
    [JsonPropertyName("full_path_taxonomy_ids")] public List<long>? FullPathTaxonomyIds { get; set; }
}

public sealed class EtsyTaxonomyProperty
{
    [JsonPropertyName("property_id")] public long PropertyId { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("display_name")] public string? DisplayName { get; set; }
    [JsonPropertyName("scales")] public List<JsonElement>? Scales { get; set; }
    [JsonPropertyName("is_required")] public bool IsRequired { get; set; }
    [JsonPropertyName("supports_attributes")] public bool SupportsAttributes { get; set; }
    [JsonPropertyName("supports_variations")] public bool SupportsVariations { get; set; }
    [JsonPropertyName("is_multivalued")] public bool IsMultivalued { get; set; }
    [JsonPropertyName("max_values_allowed")] public int? MaxValuesAllowed { get; set; }
    [JsonPropertyName("possible_values")] public List<JsonElement>? PossibleValues { get; set; }
}
