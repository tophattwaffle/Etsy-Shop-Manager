namespace EtsyShopManager.Core.Domain;

// Per-shop lookups cached from Etsy. Listings refer to them by Etsy id (not a foreign key), so a refresh can
// replace them without touching listings. Each keeps the raw JSON for fields the UI doesn't show yet.

public class ShippingProfile
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public long EtsyShippingProfileId { get; set; }
    public string Title { get; set; } = "";
    public string? OriginCountryIso { get; set; }
    public string? OriginPostalCode { get; set; }
    public string? ProfileType { get; set; }
    public int DestinationCount { get; set; }
    public int UpgradeCount { get; set; }
    public string RawJson { get; set; } = "";
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>A processing profile (Etsy: readiness state definition), referenced by readiness_state_id.</summary>
public class ProcessingProfile
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public long ReadinessStateId { get; set; }
    public string ReadinessState { get; set; } = "";
    public int MinProcessingDays { get; set; }
    public int MaxProcessingDays { get; set; }
    public string? DisplayLabel { get; set; }
    public string RawJson { get; set; } = "";
    public DateTime ImportedAtUtc { get; set; }
}

public class ReturnPolicy
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public long EtsyReturnPolicyId { get; set; }
    public bool AcceptsReturns { get; set; }
    public bool AcceptsExchanges { get; set; }
    public int? ReturnDeadlineDays { get; set; }
    public string RawJson { get; set; } = "";
    public DateTime ImportedAtUtc { get; set; }
}

public class ShopSection
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public long EtsySectionId { get; set; }
    public string Title { get; set; } = "";
    public int Rank { get; set; }
    public int ActiveListingCount { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

public class ProductionPartner
{
    public int Id { get; set; }
    public int ShopId { get; set; }
    public long EtsyPartnerId { get; set; }
    public string Name { get; set; } = "";
    public string? Location { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>Etsy seller taxonomy node (shared by all shops). The key is Etsy's taxonomy id.</summary>
public class TaxonomyNode
{
    public long Id { get; set; }
    public long? ParentId { get; set; }
    public int Level { get; set; }
    public string Name { get; set; } = "";

    /// <summary>"Home &amp; Living &gt; Home Décor &gt; Vases", for display and search.</summary>
    public string FullPath { get; set; } = "";
    public bool HasChildren { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}

/// <summary>A property Etsy offers for a taxonomy node (attributes and variations). Fetched for taxonomies in use.</summary>
public class TaxonomyProperty
{
    public long TaxonomyId { get; set; }
    public long PropertyId { get; set; }
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public bool IsRequired { get; set; }
    public bool SupportsAttributes { get; set; }
    public bool SupportsVariations { get; set; }
    public bool IsMultivalued { get; set; }
    public int? MaxValuesAllowed { get; set; }

    /// <summary>Scales and possible values as JSON arrays (can be long).</summary>
    public string ScalesJson { get; set; } = "[]";
    public string PossibleValuesJson { get; set; } = "[]";
    public int PossibleValueCount { get; set; }
    public DateTime ImportedAtUtc { get; set; }
}
