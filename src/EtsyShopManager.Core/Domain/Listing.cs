namespace EtsyShopManager.Core.Domain;

/// <summary>
/// A listing as imported from Etsy. In Phase 1 this mirrors Etsy; from Phase 3 on it becomes the local master copy
/// and <see cref="EtsyState"/> keeps the last state seen on Etsy, for drift detection.
/// </summary>
public class Listing
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public Shop Shop { get; set; } = null!;

    /// <summary>Etsy's listing_id.</summary>
    public long EtsyListingId { get; set; }

    /// <summary>active, inactive, sold_out, draft, expired (Etsy's state names).</summary>
    public string State { get; set; } = "";

    public string Title { get; set; } = "";
    public string Description { get; set; } = "";

    /// <summary>Base price in the currency's minor units: <see cref="PriceAmount"/> / <see cref="PriceDivisor"/>.</summary>
    public long PriceAmount { get; set; }
    public int PriceDivisor { get; set; } = 100;
    public string? CurrencyCode { get; set; }

    public int Quantity { get; set; }

    /// <summary>All SKUs on the listing's products (Etsy's "skus"), for search.</summary>
    public List<string> Skus { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Materials { get; set; } = [];
    public List<string> Styles { get; set; } = [];

    public long? TaxonomyId { get; set; }
    public long? ShippingProfileId { get; set; }
    public long? ReturnPolicyId { get; set; }
    public long? ReadinessStateId { get; set; }
    public long? ShopSectionId { get; set; }
    public int? FeaturedRank { get; set; }

    public string? WhoMade { get; set; }
    public string? WhenMade { get; set; }
    public bool? IsSupply { get; set; }
    public string? ListingType { get; set; }
    public bool IsCustomizable { get; set; }
    public bool IsPersonalizable { get; set; }
    public bool IsTaxable { get; set; }
    public bool ShouldAutoRenew { get; set; }
    public bool HasVariations { get; set; }
    public int? ProcessingMin { get; set; }
    public int? ProcessingMax { get; set; }

    public decimal? ItemWeight { get; set; }
    public string? ItemWeightUnit { get; set; }
    public decimal? ItemLength { get; set; }
    public decimal? ItemWidth { get; set; }
    public decimal? ItemHeight { get; set; }
    public string? ItemDimensionsUnit { get; set; }

    public List<long> ProductionPartnerIds { get; set; } = [];

    /// <summary>Personalization questions as Etsy returns them (JSON array), or null.</summary>
    public string? PersonalizationJson { get; set; }

    /// <summary>Category attributes (getListingProperties).</summary>
    public List<PropertyValue> Attributes { get; set; } = [];

    /// <summary>Which properties drive price / quantity / SKU / processing profile in the inventory grid.</summary>
    public List<long> PriceOnProperty { get; set; } = [];
    public List<long> QuantityOnProperty { get; set; } = [];
    public List<long> SkuOnProperty { get; set; } = [];
    public List<long> ReadinessStateOnProperty { get; set; } = [];

    public string? Url { get; set; }
    public int? Views { get; set; }
    public int? NumFavorers { get; set; }

    public DateTime? EtsyCreatedAtUtc { get; set; }
    public DateTime? EtsyUpdatedAtUtc { get; set; }
    public DateTime? EtsyStateChangedAtUtc { get; set; }
    public DateTime? EtsyEndsAtUtc { get; set; }

    public DateTime FirstImportedAtUtc { get; set; }
    public DateTime LastImportedAtUtc { get; set; }

    /// <summary>When <see cref="Attributes"/> was last fetched (they need a separate call per listing).</summary>
    public DateTime? AttributesFetchedAtUtc { get; set; }

    /// <summary>The import run that last saw this listing on Etsy.</summary>
    public string? LastSeenRunId { get; set; }

    /// <summary>A complete import did not find the listing on Etsy (deleted or removed). Local data is kept.</summary>
    public bool IsMissingOnEtsy { get; set; }

    public List<ListingImage> Images { get; set; } = [];
    public List<ListingProduct> Products { get; set; } = [];
    public ListingEtsyState? EtsyState { get; set; }
}

/// <summary>A property and its values, as used by listing attributes and inventory products.</summary>
public sealed record PropertyValue
{
    public long PropertyId { get; init; }
    public string? PropertyName { get; init; }
    public long? ScaleId { get; init; }
    public string? ScaleName { get; init; }
    public List<long> ValueIds { get; init; } = [];
    public List<string> Values { get; init; } = [];
}

/// <summary>
/// The last state seen on Etsy for a listing. Phase 3 drift detection compares a fresh pull against it.
/// </summary>
public class ListingEtsyState
{
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;

    /// <summary>The listing JSON exactly as Etsy returned it (with images/inventory includes).</summary>
    public string RawJson { get; set; } = "";

    /// <summary>Canonical JSON of the managed fields (see ManagedListingState). Quantity is excluded.</summary>
    public string ManagedJson { get; set; } = "";

    /// <summary>SHA-256 (hex) of <see cref="ManagedJson"/>.</summary>
    public string ManagedHash { get; set; } = "";

    /// <summary>Version of the managed-field definition the hash was computed with.</summary>
    public int ManagedVersion { get; set; }

    public DateTime FetchedAtUtc { get; set; }

    /// <summary>When <see cref="ManagedHash"/> last changed.</summary>
    public DateTime ManagedChangedAtUtc { get; set; }
}

public class ListingImage
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;

    public long EtsyImageId { get; set; }
    public int Rank { get; set; }
    public string? AltText { get; set; }
    public int? FullWidth { get; set; }
    public int? FullHeight { get; set; }
    public string? HexCode { get; set; }

    /// <summary>Etsy CDN URLs the files were downloaded from.</summary>
    public string SourceUrl { get; set; } = "";
    public string? SourceThumbUrl { get; set; }

    /// <summary>Content-hashed file names in the image store; null until downloaded.</summary>
    public string? FileName { get; set; }
    public string? ThumbFileName { get; set; }
    public long? ByteSize { get; set; }
    public DateTime? DownloadedAtUtc { get; set; }
    public string? DownloadError { get; set; }
}

/// <summary>One combination of variation values. Etsy replaces the whole inventory per write.</summary>
public class ListingProduct
{
    public int Id { get; set; }
    public int ListingId { get; set; }
    public Listing Listing { get; set; } = null!;

    public long EtsyProductId { get; set; }
    public string? Sku { get; set; }
    public bool IsDeleted { get; set; }

    /// <summary>Up to two variation properties with one value each.</summary>
    public List<PropertyValue> PropertyValues { get; set; } = [];

    public List<ListingOffering> Offerings { get; set; } = [];
}

public class ListingOffering
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public ListingProduct Product { get; set; } = null!;

    public long EtsyOfferingId { get; set; }
    public long PriceAmount { get; set; }
    public int PriceDivisor { get; set; } = 100;
    public string? CurrencyCode { get; set; }
    public int Quantity { get; set; }
    public bool IsEnabled { get; set; }
    public bool IsDeleted { get; set; }
    public long? ReadinessStateId { get; set; }
}

/// <summary>Import progress per shop. Lets an interrupted import resume where it stopped.</summary>
public class ShopSyncState
{
    public int ShopId { get; set; }
    public Shop Shop { get; set; } = null!;

    /// <summary>The run in progress, or the last completed one.</summary>
    public string? RunId { get; set; }
    public DateTime? RunStartedAtUtc { get; set; }
    public bool RunForce { get; set; }

    /// <summary>Where the listing pull is (JSON), so a restarted job continues from that page. Null when no run is open.</summary>
    public string? CursorJson { get; set; }

    public DateTime? LastCompletedAtUtc { get; set; }
    public DateTime? LookupsImportedAtUtc { get; set; }
    public int? LastListingCount { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAtUtc { get; set; }

    public bool IsRunOpen => CursorJson is not null;
}
