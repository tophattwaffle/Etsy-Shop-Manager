using System.Collections.Concurrent;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Listings;

/// <summary>
/// Same-origin copies of shop profile images, kept in the <see cref="ImageStore"/>. The listings page reads each icon's
/// pixels in the browser to pick the shop's accent colour, which a canvas only allows for same-origin images.
/// </summary>
public sealed class ShopIconCache(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ImageStore store,
    ILogger<ShopIconCache> logger)
{
    public const string UrlPrefix = "/media/shop-icons/";

    // Etsy icon URL -> stored file name. A new URL (the shop changed its icon) is fetched again, and so is a file the
    // image store's orphan clean-up removed (no listing image refers to it).
    private readonly ConcurrentDictionary<string, string> _files = new();

    public static string UrlFor(int shopId) => UrlPrefix.TrimStart('/') + shopId;

    /// <summary>The stored icon's file name, fetching it from Etsy's CDN the first time. Null if the shop has none.</summary>
    public async Task<string?> GetFileNameAsync(int shopId, CancellationToken ct = default)
    {
        string? iconUrl;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            iconUrl = await db.Shops.Where(s => s.Id == shopId).Select(s => s.IconUrl).FirstOrDefaultAsync(ct);
        if (string.IsNullOrEmpty(iconUrl)) return null;

        if (_files.TryGetValue(iconUrl, out var cached) && store.Exists(cached)) return cached;

        try
        {
            var client = httpClientFactory.CreateClient(EtsyServiceCollectionExtensions.EtsyImageClientName);
            using var response = await client.GetAsync(iconUrl, ct);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length == 0) return null;
            var name = await store.SaveAsync(bytes, ImageStore.ExtensionFor(response.Content.Headers.ContentType?.MediaType, iconUrl), ct);
            _files[iconUrl] = name;
            return name;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                       || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogWarning("Icon for shop {ShopId} ({Url}) could not be downloaded: {Message}", shopId, iconUrl, ex.Message);
            return null;
        }
    }
}
