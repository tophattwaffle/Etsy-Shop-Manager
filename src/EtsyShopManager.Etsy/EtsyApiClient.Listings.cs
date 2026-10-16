using System.Text.Json;
using EtsyShopManager.Etsy.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace EtsyShopManager.Etsy;

/// <summary>Listing, inventory, shop lookup and taxonomy reads (Phase 1).</summary>
public sealed partial class EtsyApiClient
{
    /// <summary>Etsy's maximum page size for getListingsByShop.</summary>
    public const int MaxListingsPageSize = 100;

    /// <summary>The includes requested on listing pulls, which save one call per listing for each.</summary>
    public static readonly string[] ListingIncludes = ["Images", "Inventory", "Personalization"];

    /// <summary>One page of a shop's listings in one state, with the raw JSON of each listing.</summary>
    public Task<EtsyRawPage<EtsyListing>> GetListingsByShopAsync(long etsyShopId, string state, int offset, int limit,
        EtsyAuth auth, CancellationToken ct = default)
    {
        var path = QueryHelpers.AddQueryString($"/v3/application/shops/{etsyShopId}/listings", new Dictionary<string, string?>
        {
            ["state"] = state,
            ["limit"] = Math.Clamp(limit, 1, MaxListingsPageSize).ToString(),
            ["offset"] = Math.Max(0, offset).ToString(),
            // Oldest first, so listings created during a pull land on later pages instead of shifting earlier ones.
            ["sort_on"] = "created",
            ["sort_order"] = "asc",
            ["includes"] = string.Join(",", ListingIncludes),
        });
        return GetRawPageAsync<EtsyListing>(path, auth, ct);
    }

    public async Task<List<EtsyListingImage>> GetListingImagesAsync(long listingId, EtsyAuth auth, CancellationToken ct = default) =>
        (await GetAsync<EtsyPage<EtsyListingImage>>($"/v3/application/listings/{listingId}/images", auth, ct)).Results;

    public Task<EtsyInventory> GetListingInventoryAsync(long listingId, EtsyAuth auth, CancellationToken ct = default) =>
        GetAsync<EtsyInventory>($"/v3/application/listings/{listingId}/inventory", auth, ct);

    /// <summary>Category attributes (getListingProperties). Not available as a listing include.</summary>
    public async Task<List<EtsyPropertyValue>> GetListingPropertiesAsync(long etsyShopId, long listingId, EtsyAuth auth,
        CancellationToken ct = default) =>
        (await GetAsync<EtsyPage<EtsyPropertyValue>>(
            $"/v3/application/shops/{etsyShopId}/listings/{listingId}/properties", auth, ct)).Results;

    public async Task<List<EtsyRaw<EtsyShippingProfile>>> GetShippingProfilesAsync(long etsyShopId, EtsyAuth auth,
        CancellationToken ct = default) =>
        (await GetRawPageAsync<EtsyShippingProfile>($"/v3/application/shops/{etsyShopId}/shipping-profiles", auth, ct)).Results;

    /// <summary>Processing profiles (readiness state definitions). Paged, unlike the other lookups.</summary>
    public async Task<List<EtsyRaw<EtsyProcessingProfile>>> GetProcessingProfilesAsync(long etsyShopId, EtsyAuth auth,
        CancellationToken ct = default)
    {
        const int limit = 100;
        var all = new List<EtsyRaw<EtsyProcessingProfile>>();
        for (var offset = 0; ; offset += limit)
        {
            var page = await GetRawPageAsync<EtsyProcessingProfile>(
                $"/v3/application/shops/{etsyShopId}/readiness-state-definitions?limit={limit}&offset={offset}", auth, ct);
            all.AddRange(page.Results);
            if (page.Results.Count < limit || all.Count >= page.Count) return all;
        }
    }

    public async Task<List<EtsyRaw<EtsyReturnPolicy>>> GetReturnPoliciesAsync(long etsyShopId, EtsyAuth auth,
        CancellationToken ct = default) =>
        (await GetRawPageAsync<EtsyReturnPolicy>($"/v3/application/shops/{etsyShopId}/policies/return", auth, ct)).Results;

    public async Task<List<EtsyShopSection>> GetShopSectionsAsync(long etsyShopId, EtsyAuth auth, CancellationToken ct = default) =>
        (await GetAsync<EtsyPage<EtsyShopSection>>($"/v3/application/shops/{etsyShopId}/sections", auth, ct)).Results;

    public async Task<List<EtsyProductionPartner>> GetProductionPartnersAsync(long etsyShopId, EtsyAuth auth,
        CancellationToken ct = default) =>
        (await GetAsync<EtsyPage<EtsyProductionPartner>>($"/v3/application/shops/{etsyShopId}/production-partners", auth, ct)).Results;

    /// <summary>The full seller taxonomy as a tree (one call).</summary>
    public async Task<List<EtsyTaxonomyNode>> GetSellerTaxonomyNodesAsync(EtsyAuth auth, CancellationToken ct = default) =>
        (await GetAsync<EtsyPage<EtsyTaxonomyNode>>("/v3/application/seller-taxonomy/nodes", auth, ct)).Results;

    public async Task<List<EtsyTaxonomyProperty>> GetTaxonomyPropertiesAsync(long taxonomyId, EtsyAuth auth,
        CancellationToken ct = default) =>
        (await GetAsync<EtsyPage<EtsyTaxonomyProperty>>(
            $"/v3/application/seller-taxonomy/nodes/{taxonomyId}/properties", auth, ct)).Results;

    /// <summary>Reads a collection response and keeps the raw JSON of each item next to the typed value.</summary>
    public async Task<EtsyRawPage<T>> GetRawPageAsync<T>(string relativePath, EtsyAuth auth, CancellationToken ct = default)
    {
        var page = await GetAsync<EtsyPage<JsonElement>>(relativePath, auth, ct);
        var results = page.Results
            .Select(e => new EtsyRaw<T>(
                e.Deserialize<T>(EtsyJson.Options) ?? throw new JsonException($"Null item in {relativePath}"),
                e.GetRawText()))
            .ToList();
        return new EtsyRawPage<T>(page.Count, results);
    }
}
