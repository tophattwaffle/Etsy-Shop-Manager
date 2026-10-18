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
    /// A buyer's profile (getUser): the shop's token may read the buyers who bought from it, and needs the email_r scope.
    /// Used for the buyer's first name on gift slips ("- Kate"), which Etsy's receipts leave out (gift_sender is empty).
    /// </summary>
    public Task<EtsyUser> GetUserAsync(long userId, EtsyAuth auth, CancellationToken ct = default) =>
        GetAsync<EtsyUser>($"/v3/application/users/{userId}", auth, ct);

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
