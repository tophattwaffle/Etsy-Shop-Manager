using System.Net.Http.Json;
using System.Text.Json;
using EtsyShopManager.Etsy.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace EtsyShopManager.Etsy;

/// <summary>Listing reads for a single listing, and the write calls (Phase 3).</summary>
public sealed partial class EtsyApiClient
{
    /// <summary>One listing with its images and personalization (inventory needs its own call).</summary>
    public async Task<EtsyRaw<EtsyListing>> GetListingAsync(long listingId, EtsyAuth auth, CancellationToken ct = default)
    {
        var path = QueryHelpers.AddQueryString($"/v3/application/listings/{listingId}", "includes", "Images,Personalization");
        var element = await GetAsync<JsonElement>(path, auth, ct);
        return new EtsyRaw<EtsyListing>(
            element.Deserialize<EtsyListing>(EtsyJson.Options) ?? throw new JsonException($"Empty listing {listingId}"),
            element.GetRawText());
    }

    /// <summary>The live inventory, with the listing itself (for its current state) when asked.</summary>
    public Task<EtsyInventory> GetListingInventoryAsync(long listingId, bool includeListing, EtsyAuth auth,
        CancellationToken ct = default) =>
        GetAsync<EtsyInventory>(includeListing
            ? $"/v3/application/listings/{listingId}/inventory?includes=Listing"
            : $"/v3/application/listings/{listingId}/inventory", auth, ct);

    /// <summary>
    /// updateListing (PATCH, form-encoded). Only the fields given are changed. Array fields are comma-separated.
    /// </summary>
    public async Task<EtsyRaw<EtsyListing>> UpdateListingAsync(long etsyShopId, long listingId,
        IReadOnlyList<KeyValuePair<string, string>> fields, EtsyAuth auth, CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch,
            $"/v3/application/shops/{etsyShopId}/listings/{listingId}")
        {
            Content = new FormUrlEncodedContent(fields),
        }, auth, ct);
        var element = await response.Content.ReadFromJsonAsync<JsonElement>(EtsyJson.Options, ct);
        return new EtsyRaw<EtsyListing>(element.Deserialize<EtsyListing>(EtsyJson.Options)!, element.GetRawText());
    }

    /// <summary>updateListingInventory (PUT). Replaces the whole inventory; returns the new one.</summary>
    public async Task<EtsyInventory> UpdateListingInventoryAsync(long listingId, EtsyInventoryUpdate inventory, EtsyAuth auth,
        CancellationToken ct = default)
    {
        using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, $"/v3/application/listings/{listingId}/inventory")
        {
            Content = JsonContent.Create(inventory, options: EtsyJson.Options),
        }, auth, ct);
        return await response.Content.ReadFromJsonAsync<EtsyInventory>(EtsyJson.Options, ct)
               ?? throw new JsonException($"Empty inventory response for listing {listingId}");
    }
}
