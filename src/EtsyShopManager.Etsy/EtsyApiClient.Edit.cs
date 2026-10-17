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

    /// <summary>
    /// createDraftListing (POST, form-encoded): a new listing in the draft state (not visible to buyers; Etsy charges its
    /// listing fee only when it is published). Array fields are comma-separated, as for updateListing.
    /// </summary>
    public async Task<EtsyRaw<EtsyListing>> CreateDraftListingAsync(long etsyShopId, IReadOnlyList<KeyValuePair<string, string>> fields,
        EtsyAuth auth, CancellationToken ct = default)
    {
        using var response = await SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/application/shops/{etsyShopId}/listings")
            {
                Content = new FormUrlEncodedContent(fields),
            };
            request.Options.Set(Http.EtsyRetryHandler.NoRetry, true); // never create the same draft twice
            return request;
        }, auth, ct);
        var element = await response.Content.ReadFromJsonAsync<JsonElement>(EtsyJson.Options, ct);
        return new EtsyRaw<EtsyListing>(element.Deserialize<EtsyListing>(EtsyJson.Options)!, element.GetRawText());
    }

    /// <summary>updateListingProperty (PUT, form-encoded): sets one category attribute of a listing.</summary>
    public async Task UpdateListingPropertyAsync(long etsyShopId, long listingId, long propertyId, IReadOnlyList<long> valueIds,
        IReadOnlyList<string> values, long? scaleId, EtsyAuth auth, CancellationToken ct = default)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("value_ids", string.Join(",", valueIds)),
            new("values", string.Join(",", values)),
        };
        if (scaleId is { } scale) fields.Add(new("scale_id", scale.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put,
            $"/v3/application/shops/{etsyShopId}/listings/{listingId}/properties/{propertyId}")
        {
            Content = new FormUrlEncodedContent(fields),
        }, auth, ct);
    }

    /// <summary>deleteListingProperty (DELETE): removes one category attribute from a listing.</summary>
    public async Task DeleteListingPropertyAsync(long etsyShopId, long listingId, long propertyId, EtsyAuth auth, CancellationToken ct = default)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete,
            $"/v3/application/shops/{etsyShopId}/listings/{listingId}/properties/{propertyId}"), auth, ct);
    }

    /// <summary>
    /// updateListingPersonalization (POST, JSON): replaces all of a listing's personalization questions. Sent with
    /// supports_multiple_personalization_questions=true, because the app reads and writes the full question list (up to
    /// five questions of every type); without it Etsy only takes one text question.
    /// </summary>
    public async Task UpdateListingPersonalizationAsync(long etsyShopId, long listingId, EtsyPersonalizationUpdate personalization,
        EtsyAuth auth, CancellationToken ct = default)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post,
            $"/v3/application/shops/{etsyShopId}/listings/{listingId}/personalization?supports_multiple_personalization_questions=true")
        {
            Content = JsonContent.Create(personalization, options: EtsyJson.Options),
        }, auth, ct);
    }

    /// <summary>deleteListingPersonalization (DELETE): the listing no longer asks buyers for personalization.</summary>
    public async Task DeleteListingPersonalizationAsync(long etsyShopId, long listingId, EtsyAuth auth, CancellationToken ct = default)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete,
            $"/v3/application/shops/{etsyShopId}/listings/{listingId}/personalization"), auth, ct);
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

    /// <summary>uploadListingImage (POST, multipart) with a new photo file, at <paramref name="rank"/> (the app sends the end).</summary>
    public async Task<EtsyListingImage> UploadListingImageAsync(long etsyShopId, long listingId, byte[] image, string fileName, int rank,
        string? altText, EtsyAuth auth, CancellationToken ct = default)
    {
        using var response = await SendAsync(() =>
        {
            var file = new ByteArrayContent(image);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".png" => "image/png",
                ".gif" => "image/gif",
                ".webp" => "image/webp",
                _ => "image/jpeg",
            });
            var form = new MultipartFormDataContent
            {
                { file, "image", fileName },
                { new StringContent(rank.ToString(System.Globalization.CultureInfo.InvariantCulture)), "rank" },
                { new StringContent("false"), "overwrite" },
            };
            if (!string.IsNullOrEmpty(altText)) form.Add(new StringContent(altText), "alt_text");
            var request = new HttpRequestMessage(HttpMethod.Post, $"/v3/application/shops/{etsyShopId}/listings/{listingId}/images") { Content = form };
            request.Options.Set(Http.EtsyRetryHandler.NoRetry, true); // never upload a photo twice
            return request;
        }, auth, ct);
        return await response.Content.ReadFromJsonAsync<EtsyListingImage>(EtsyJson.Options, ct)
               ?? throw new JsonException($"Empty image response for listing {listingId}");
    }

    /// <summary>deleteListingImage (DELETE): takes the photo off the listing (Etsy keeps it, so it can be re-attached by id).</summary>
    public async Task DeleteListingImageAsync(long etsyShopId, long listingId, long listingImageId, EtsyAuth auth, CancellationToken ct = default)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete,
            $"/v3/application/shops/{etsyShopId}/listings/{listingId}/images/{listingImageId}"), auth, ct);
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
