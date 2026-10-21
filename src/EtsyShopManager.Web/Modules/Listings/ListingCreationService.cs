using System.Globalization;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace EtsyShopManager.Web.Modules.Listings;

/// <summary>
/// Inputs for creating a brand new listing on Etsy from scratch.
/// </summary>
public sealed record NewListingModel
{
    public int ShopId { get; init; }
    public string Title { get; init; } = "";
    public decimal Price { get; init; }
    public int Quantity { get; init; } = 1;
    public long? TaxonomyId { get; init; }
    public long? ShippingProfileId { get; init; }
    public long? ReadinessStateId { get; init; }
    public long? ReturnPolicyId { get; init; }
    public long? ShopSectionId { get; init; }
    public string WhoMade { get; init; } = "i_did";
    public string WhenMade { get; init; } = "made_to_order";
    public bool IsSupply { get; init; } = false;
    public string ListingType { get; init; } = "physical";
    public string Description { get; init; } = "";
    public IReadOnlyList<string> Tags { get; init; } = [];
    public IReadOnlyList<string> Materials { get; init; } = [];
}

/// <summary>
/// Creates new listings from scratch as Etsy drafts (never active), imports them into the local database,
/// and audits the creation.
/// </summary>
public sealed class ListingCreationService(
    IDbContextFactory<AppDbContext> dbFactory,
    ListingImporter importer,
    EtsyApiClient etsy,
    IAuditLog audit,
    ILogger<ListingCreationService> logger)
{
    /// <summary>
    /// Validates required fields, text rules, and values before calling Etsy.
    /// Returns any human-readable problem descriptions (empty if valid).
    /// </summary>
    public IReadOnlyList<string> Validate(NewListingModel model, EditorLookups? lookups = null)
    {
        var problems = new List<string>();

        problems.AddRange(ListingTextRules.TitleProblems(model.Title ?? ""));

        if (model.Tags is { Count: > ListingEditor.MaxTags })
            problems.Add($"Etsy allows at most {ListingEditor.MaxTags} tags.");

        if (model.Tags is not null)
        {
            foreach (var tag in model.Tags.Where(t => t is not null && t.Length > ListingEditor.MaxTagLength))
                problems.Add($"The tag \"{tag}\" is longer than {ListingEditor.MaxTagLength} characters.");
        }

        problems.AddRange(ListingTextRules.TagProblems(model.Tags ?? []));

        if (model.Materials is { Count: > ListingEditor.MaxMaterials })
            problems.Add($"Etsy allows at most {ListingEditor.MaxMaterials} materials.");

        problems.AddRange(ListingTextRules.MaterialProblems(model.Materials ?? []));

        if (model.Price <= 0)
            problems.Add("Price must be greater than 0.");
        if (model.Quantity < 1)
            problems.Add("Quantity must be at least 1.");
        if (model.TaxonomyId is null or <= 0)
            problems.Add("Choose a category.");
        if (model.ShippingProfileId is null or <= 0)
            problems.Add("Choose a shipping profile.");
        if (model.ReadinessStateId is null or <= 0)
            problems.Add("Choose a processing profile.");

        if (lookups is not null)
        {
            if (model.ShippingProfileId is { } sp && lookups.ShippingProfiles.Count > 0 && !lookups.ShippingProfiles.ContainsKey(sp))
                problems.Add("Choose a shipping profile belonging to this shop.");
            if (model.ReadinessStateId is { } rp && lookups.ProcessingProfiles.Count > 0 && !lookups.ProcessingProfiles.ContainsKey(rp))
                problems.Add("Choose a processing profile belonging to this shop.");
            if (model.ReturnPolicyId is { } ret && lookups.ReturnPolicies.Count > 0 && !lookups.ReturnPolicies.ContainsKey(ret))
                problems.Add("The return policy doesn't belong to this shop.");
            if (model.ShopSectionId is { } sec && lookups.Sections.Count > 0 && !lookups.Sections.ContainsKey(sec))
                problems.Add("The shop section doesn't belong to this shop.");
        }

        return problems;
    }

    /// <summary>
    /// Creates a draft listing on Etsy via createDraftListing, imports it locally, and records an audit log.
    /// Never publishes the listing as active.
    /// Returns the newly created local listing database ID.
    /// </summary>
    public async Task<int> CreateDraftAsync(NewListingModel model, string? user = null, CancellationToken ct = default)
    {
        var problems = Validate(model);
        if (problems.Count > 0)
            throw new InvalidOperationException($"Cannot create listing: {string.Join("; ", problems)}");

        Shop shop;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            shop = await db.Shops.Include(s => s.Token).FirstOrDefaultAsync(s => s.Id == model.ShopId, ct)
                   ?? throw new InvalidOperationException($"Shop {model.ShopId} not found.");
            if (shop.Token is null || shop.Token.NeedsReauth || !shop.IsActive)
                throw new InvalidOperationException($"{shop.Name} must be reconnected to Etsy first.");
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("title", model.Title.Trim()),
            new("price", model.Price.ToString(CultureInfo.InvariantCulture)),
            new("quantity", model.Quantity.ToString(CultureInfo.InvariantCulture)),
            new("taxonomy_id", model.TaxonomyId!.Value.ToString(CultureInfo.InvariantCulture)),
            new("shipping_profile_id", model.ShippingProfileId!.Value.ToString(CultureInfo.InvariantCulture)),
            new("readiness_state_id", model.ReadinessStateId!.Value.ToString(CultureInfo.InvariantCulture)),
            new("who_made", string.IsNullOrWhiteSpace(model.WhoMade) ? "i_did" : model.WhoMade),
            new("when_made", string.IsNullOrWhiteSpace(model.WhenMade) ? "made_to_order" : model.WhenMade),
            new("is_supply", model.IsSupply ? "true" : "false"),
            new("type", string.IsNullOrWhiteSpace(model.ListingType) ? "physical" : model.ListingType),
        };

        if (model.ReturnPolicyId is { } ret)
            form.Add(new("return_policy_id", ret.ToString(CultureInfo.InvariantCulture)));
        if (model.ShopSectionId is { } sec)
            form.Add(new("shop_section_id", sec.ToString(CultureInfo.InvariantCulture)));
        if (!string.IsNullOrEmpty(model.Description))
            form.Add(new("description", model.Description));
        if (model.Tags is { Count: > 0 })
            form.Add(new("tags", string.Join(",", model.Tags.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()))));
        if (model.Materials is { Count: > 0 })
            form.Add(new("materials", string.Join(",", model.Materials.Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => m.Trim()))));

        logger.LogInformation("Creating draft listing '{Title}' in shop {ShopName} ({EtsyShopId})", model.Title, shop.Name, shop.EtsyShopId);

        var created = await etsy.CreateDraftListingAsync(shop.EtsyShopId, form, EtsyAuth.ForShop(shop.Id), ct);
        var createdEtsyId = created.Value.ListingId;

        var localId = await importer.ImportNewListingAsync(shop.Id, createdEtsyId, ct);

        await audit.WriteAsync("listing.created-draft", nameof(Listing), createdEtsyId.ToString(CultureInfo.InvariantCulture),
            $"Created draft listing {createdEtsyId} (\"{model.Title.Trim()}\") in shop {shop.Name}", user, ct);

        return localId;
    }
}
