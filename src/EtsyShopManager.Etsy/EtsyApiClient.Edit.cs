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

    /// <summary>
    /// uploadListingImage (POST, multipart) with an existing <paramref name="listingImageId"/> and a rank: Etsy's way to
    /// put a photo the listing already has at a position. How Etsy moves the other photos is not documented; it is used only
    /// by the Admin photo order test, on listings that aren't public.
    /// </summary>
    public async Task<EtsyListingImage> RepositionListingImageAsync(long etsyShopId, long listingId, long listingImageId, int rank,
        bool overwrite, string? altText, EtsyAuth auth, CancellationToken ct = default)
    {
        using var response = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent
            {
                { new StringContent(listingImageId.ToString(System.Globalization.CultureInfo.InvariantCulture)), "listing_image_id" },
                { new StringContent(rank.ToString(System.Globalization.CultureInfo.InvariantCulture)), "rank" },
                { new StringContent(overwrite ? "true" : "false"), "overwrite" },
            };
            if (altText is not null) form.Add(new StringContent(altText), "alt_text");
            return new HttpRequestMessage(HttpMethod.Post, $"/v3/application/shops/{etsyShopId}/listings/{listingId}/images") { Content = form };
        }, auth, ct);
        return await response.Content.ReadFromJsonAsync<EtsyListingImage>(EtsyJson.Options, ct)
               ?? throw new JsonException($"Empty image response for listing {listingId}");
    }
}
