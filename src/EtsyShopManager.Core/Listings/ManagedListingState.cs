using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using EtsyShopManager.Core.Domain;

namespace EtsyShopManager.Core.Listings;

/// <summary>
/// The fields this app manages on a listing, in a canonical form: the same listing always produces the same JSON
/// and hash. Phase 1 stores it for every imported listing (the last known Etsy state); Phase 3 drift detection
/// compares a fresh pull, and the renderer's output, against it.
/// <para>
/// Excluded on purpose: quantity (sales and auto-refill change it), Etsy-generated product/offering ids (Etsy
/// issues new ones on every inventory write), statistics and timestamps. <c>sold_out</c> counts as <c>active</c>,
/// because selling out is a quantity change.
/// </para>
/// </summary>
public sealed record ManagedListingState
{
    /// <summary>Bump when the field set or normalisation changes, so old hashes are not compared with new ones.</summary>
    /// <remarks>2: title, description, tags, materials and styles are HTML-decoded. 3: properties keep their name (variation
    /// names such as "Bulk Pricing", needed to add or rename variations). 4: photo alt text is HTML-decoded too.
    /// 5: attributes and personalization become editable: attributes without their name (Etsy's name for a property can
    /// differ from the taxonomy's), values in id order; personalization as decoded questions without Etsy's ids.
    /// 6: listing video state tracking.</remarks>
    public const int CurrentVersion = 6;

    public string State { get; init; } = "";
    public string Title { get; init; } = "";
    public string Description { get; init; } = "";
    public long PriceAmount { get; init; }
    public int PriceDivisor { get; init; }
    public string? CurrencyCode { get; init; }
    public List<string> Tags { get; init; } = [];
    public List<string> Materials { get; init; } = [];
    public List<string> Styles { get; init; } = [];
    public long? TaxonomyId { get; init; }
    public long? ShippingProfileId { get; init; }
    public long? ReturnPolicyId { get; init; }
    public long? ReadinessStateId { get; init; }
    public long? ShopSectionId { get; init; }
    public int? FeaturedRank { get; init; }
    public string? WhoMade { get; init; }
    public string? WhenMade { get; init; }
    public bool? IsSupply { get; init; }
    public string? ListingType { get; init; }
    public bool IsCustomizable { get; init; }
    public bool IsPersonalizable { get; init; }
    public bool IsTaxable { get; init; }
    public bool ShouldAutoRenew { get; init; }
    public decimal? ItemWeight { get; init; }
    public string? ItemWeightUnit { get; init; }
    public decimal? ItemLength { get; init; }
    public decimal? ItemWidth { get; init; }
    public decimal? ItemHeight { get; init; }
    public string? ItemDimensionsUnit { get; init; }
    public List<long> ProductionPartnerIds { get; init; } = [];
    public List<ManagedPersonalizationQuestion> Personalization { get; init; } = [];
    public List<ManagedAttribute> Attributes { get; init; } = [];
    public ManagedInventory Inventory { get; init; } = new();
    public List<ManagedImage> Images { get; init; } = [];
    public ManagedVideo? Video { get; init; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        WriteIndented = false,
    };

    public static ManagedListingState From(Listing listing) => new()
    {
        State = listing.State == "sold_out" ? "active" : listing.State,
        Title = listing.Title,
        Description = NormalizeNewlines(listing.Description),
        PriceAmount = listing.PriceAmount,
        PriceDivisor = listing.PriceDivisor,
        CurrencyCode = listing.CurrencyCode,
        Tags = [.. listing.Tags],
        Materials = [.. listing.Materials],
        Styles = [.. listing.Styles],
        TaxonomyId = listing.TaxonomyId,
        ShippingProfileId = listing.ShippingProfileId,
        ReturnPolicyId = listing.ReturnPolicyId,
        ReadinessStateId = listing.ReadinessStateId,
        ShopSectionId = listing.ShopSectionId,
        FeaturedRank = listing.FeaturedRank,
        WhoMade = listing.WhoMade,
        WhenMade = listing.WhenMade,
        IsSupply = listing.IsSupply,
        ListingType = listing.ListingType,
        IsCustomizable = listing.IsCustomizable,
        IsPersonalizable = listing.IsPersonalizable,
        IsTaxable = listing.IsTaxable,
        ShouldAutoRenew = listing.ShouldAutoRenew,
        ItemWeight = listing.ItemWeight,
        ItemWeightUnit = listing.ItemWeightUnit,
        ItemLength = listing.ItemLength,
        ItemWidth = listing.ItemWidth,
        ItemHeight = listing.ItemHeight,
        ItemDimensionsUnit = listing.ItemDimensionsUnit,
        ProductionPartnerIds = [.. listing.ProductionPartnerIds.Order()],
        Personalization = ManagedPersonalizationQuestion.Parse(listing.PersonalizationJson),
        Attributes = [.. listing.Attributes.Select(ManagedAttribute.From).OrderBy(p => p.PropertyId)],
        Inventory = ManagedInventory.From(listing),
        Images = [.. listing.Images.OrderBy(i => i.Rank).ThenBy(i => i.EtsyImageId)
            .Select(i => new ManagedImage(i.EtsyImageId, i.Rank, i.AltText ?? ""))],
        Video = listing.Videos.FirstOrDefault(v => v.VideoState != "deleted") is { } v
            ? new ManagedVideo(v.EtsyVideoId, v.VideoState ?? "active", v.Width, v.Height)
            : null,
    };

    public string ToCanonicalJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static string Hash(string canonicalJson) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));

    private static string NormalizeNewlines(string s) => s.Replace("\r\n", "\n");
}

public sealed record ManagedProperty(long PropertyId, string? PropertyName, long? ScaleId, List<long> ValueIds, List<string> Values)
{
    public static ManagedProperty From(PropertyValue v) =>
        new(v.PropertyId, v.PropertyName, v.ScaleId, [.. v.ValueIds], [.. v.Values]);

    /// <summary>Sort key for a product: its variation values, independent of Etsy's product ids.</summary>
    [JsonIgnore]
    public string Key => $"{PropertyId}:{ScaleId}:{string.Join(",", ValueIds)}:{string.Join("|", Values)}";
}

public sealed record ManagedInventory
{
    public List<long> PriceOnProperty { get; init; } = [];
    public List<long> QuantityOnProperty { get; init; } = [];
    public List<long> SkuOnProperty { get; init; } = [];
    public List<long> ReadinessStateOnProperty { get; init; } = [];
    public List<ManagedProduct> Products { get; init; } = [];

    public static ManagedInventory From(Listing listing) => new()
    {
        PriceOnProperty = [.. listing.PriceOnProperty.Order()],
        QuantityOnProperty = [.. listing.QuantityOnProperty.Order()],
        SkuOnProperty = [.. listing.SkuOnProperty.Order()],
        ReadinessStateOnProperty = [.. listing.ReadinessStateOnProperty.Order()],
        Products = [.. listing.Products
            .Where(p => !p.IsDeleted)
            .Select(p => new ManagedProduct(
                p.Sku ?? "",
                [.. p.PropertyValues.Select(ManagedProperty.From).OrderBy(v => v.PropertyId)],
                [.. p.Offerings.Where(o => !o.IsDeleted)
                    .Select(o => new ManagedOffering(o.PriceAmount, o.PriceDivisor, o.IsEnabled, o.ReadinessStateId))
                    .OrderBy(o => o.PriceAmount).ThenBy(o => o.ReadinessStateId)]))
            .OrderBy(p => string.Join(";", p.PropertyValues.Select(v => v.Key)), StringComparer.Ordinal)
            .ThenBy(p => p.Sku, StringComparer.Ordinal)],
    };
}

/// <summary>A category attribute: values with their ids, in id order (the order Etsy returns them in can vary).</summary>
public sealed record ManagedAttribute(long PropertyId, long? ScaleId, List<long> ValueIds, List<string> Values)
{
    public static ManagedAttribute From(PropertyValue v)
    {
        var pairs = v.ValueIds.Zip(v.Values).OrderBy(p => p.First).ThenBy(p => p.Second, StringComparer.Ordinal).ToList();
        // Values without an id (or ids without a value) are kept after the pairs, as Etsy gave them.
        return new(v.PropertyId, v.ScaleId,
            [.. pairs.Select(p => p.First), .. v.ValueIds.Skip(pairs.Count)],
            [.. pairs.Select(p => p.Second), .. v.Values.Skip(pairs.Count)]);
    }
}

/// <summary>
/// A personalization question as the seller sets it, without Etsy's question and option ids (Etsy may issue new ones
/// on every write). Text is HTML-decoded. <see cref="AddOnPrice"/> is in the shop's currency (e.g. 2.50).
/// </summary>
public sealed record ManagedPersonalizationQuestion(string QuestionType, string QuestionText, string? Instructions, bool Required,
    long? MaxAllowedCharacters, long? MaxAllowedFiles, decimal? AddOnPrice, List<string>? Options)
{
    /// <summary>The questions of Etsy's personalization_questions JSON (as stored by the import), in Etsy's order.</summary>
    public static List<ManagedPersonalizationQuestion> Parse(string? questionsJson)
    {
        if (string.IsNullOrWhiteSpace(questionsJson)) return [];
        using var doc = JsonDocument.Parse(questionsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
        return [.. doc.RootElement.EnumerateArray().Where(q => q.ValueKind == JsonValueKind.Object).Select(q => new ManagedPersonalizationQuestion(
            Str(q, "question_type") ?? "text_input",
            Text(Str(q, "question_text")) ?? "",
            Text(Str(q, "instructions")),
            q.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True,
            Long(q, "max_allowed_characters"),
            Long(q, "max_allowed_files"),
            Price(q),
            q.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array && o.GetArrayLength() > 0
                ? [.. o.EnumerateArray().Select(x => Text(x.ValueKind == JsonValueKind.Object ? Str(x, "label") : null) ?? "")]
                : null))];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;

    /// <summary>Plain text with Etsy's HTML encoding and Windows newlines removed; empty counts as none.</summary>
    private static string? Text(string? s) =>
        string.IsNullOrEmpty(s) ? null : System.Net.WebUtility.HtmlDecode(s).Replace("\r\n", "\n");

    /// <summary>Etsy returns the add-on price as money (amount and divisor); some replies use a plain number.</summary>
    private static decimal? Price(JsonElement q)
    {
        if (!q.TryGetProperty("add_on_price", out var p)) return null;
        if (p.ValueKind == JsonValueKind.Number) return p.GetDecimal();
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty("amount", out var a) || a.ValueKind != JsonValueKind.Number) return null;
        var divisor = p.TryGetProperty("divisor", out var d) && d.ValueKind == JsonValueKind.Number && d.GetInt64() > 0 ? d.GetInt64() : 100;
        return a.GetDecimal() / divisor;
    }
}

public sealed record ManagedProduct(string Sku, List<ManagedProperty> PropertyValues, List<ManagedOffering> Offerings);

/// <summary>An offering without its quantity.</summary>
public sealed record ManagedOffering(long PriceAmount, int PriceDivisor, bool IsEnabled, long? ReadinessStateId);

public sealed record ManagedImage(long EtsyImageId, int Rank, string AltText);

public sealed record ManagedVideo(long EtsyVideoId, string VideoState, int? Width, int? Height);
