using System.Text.Json;
using EtsyShopManager.Etsy.Models;
using Microsoft.AspNetCore.WebUtilities;

namespace EtsyShopManager.Etsy;

/// <summary>Order reads (Phase 2).</summary>
public sealed partial class EtsyApiClient
{
    public const int MaxReceiptsPageSize = 100;
    public const int MaxBatchListingIds = 100;

    /// <summary>
    /// One page of a shop's receipts modified at or after <paramref name="minLastModifiedUtc"/> (all receipts when
    /// null), oldest first by creation. Creation order is stable while receipts are being modified, so paging by
    /// offset neither skips nor reorders them (update order would move a modified receipt to the end).
    /// </summary>
    public Task<EtsyRawPage<EtsyReceipt>> GetShopReceiptsAsync(long etsyShopId, DateTime? minLastModifiedUtc, int offset,
        int limit, EtsyAuth auth, CancellationToken ct = default)
    {
        var query = new Dictionary<string, string?>
        {
            ["limit"] = Math.Clamp(limit, 1, MaxReceiptsPageSize).ToString(),
            ["offset"] = Math.Max(0, offset).ToString(),
            ["sort_on"] = "created",
            ["sort_order"] = "asc",
        };
        if (minLastModifiedUtc is { } since)
            query["min_last_modified"] = new DateTimeOffset(DateTime.SpecifyKind(since, DateTimeKind.Utc)).ToUnixTimeSeconds().ToString();
        return GetRawPageAsync<EtsyReceipt>(
            QueryHelpers.AddQueryString($"/v3/application/shops/{etsyShopId}/receipts", query), auth, ct);
    }

    /// <summary>
    /// One receipt (getShopReceipt). Used to re-read gift orders whose receipt in the list came without the gift sender.
    /// </summary>
    public async Task<EtsyRaw<EtsyReceipt>> GetShopReceiptAsync(long etsyShopId, long receiptId, EtsyAuth auth, CancellationToken ct = default)
    {
        var element = await GetAsync<JsonElement>($"/v3/application/shops/{etsyShopId}/receipts/{receiptId}", auth, ct);
        return new EtsyRaw<EtsyReceipt>(
            element.Deserialize<EtsyReceipt>(EtsyJson.Options) ?? throw new JsonException($"Empty receipt {receiptId}"),
            element.GetRawText());
    }

    /// <summary>Up to 100 listings by id (any state the seller can see), with images.</summary>
    public async Task<List<EtsyListing>> GetListingsByIdsAsync(IReadOnlyCollection<long> listingIds, EtsyAuth auth,
        CancellationToken ct = default)
    {
        if (listingIds.Count == 0) return [];
        var path = QueryHelpers.AddQueryString("/v3/application/listings/batch", new Dictionary<string, string?>
        {
            ["listing_ids"] = string.Join(",", listingIds.Take(MaxBatchListingIds)),
            ["includes"] = "Images",
        });
        return (await GetAsync<EtsyPage<EtsyListing>>(path, auth, ct)).Results;
    }
}
