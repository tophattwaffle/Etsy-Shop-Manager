using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Listings;

/// <param name="Gone">Images Etsy no longer serves (404/410): recorded on the image, not retried as failures.</param>
public sealed record ImageDownloadResult(int Downloaded, int Failed, int Pending, int Gone = 0);

/// <summary>
/// Downloads listing photos (full size plus a 570px display copy) from Etsy's CDN into the <see cref="ImageStore"/>.
/// Works from the DB: any image row without a stored file (or whose file was deleted) is fetched, so a re-run
/// simply continues where the last one stopped.
/// </summary>
public sealed class ImageDownloader(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ImageStore store,
    TimeProvider time,
    ILogger<ImageDownloader> logger)
{
    /// <summary>Files nothing refers to are deleted once they are this old (gives in-flight work a margin).</summary>
    public static readonly TimeSpan OrphanMinAge = TimeSpan.FromDays(1);

    public async Task<ImageDownloadResult> DownloadForShopAsync(int shopId, CancellationToken ct)
    {
        List<int> pending;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rows = await db.ListingImages.AsNoTracking()
                .Where(i => i.Listing.ShopId == shopId)
                .Select(i => new { i.Id, i.FileName, i.ThumbFileName, i.SourceThumbUrl })
                .ToListAsync(ct);
            pending = rows
                .Where(r => !store.Exists(r.FileName) || (r.SourceThumbUrl is not null && !store.Exists(r.ThumbFileName)))
                .Select(r => r.Id).ToList();
        }

        var client = httpClientFactory.CreateClient(EtsyServiceCollectionExtensions.EtsyImageClientName);
        int downloaded = 0, failed = 0, gone = 0;
        foreach (var id in pending)
        {
            ct.ThrowIfCancellationRequested();
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var image = await db.ListingImages.FirstOrDefaultAsync(i => i.Id == id, ct);
            if (image is null) continue;

            try
            {
                if (!store.Exists(image.FileName))
                {
                    var (name, size) = await FetchAsync(client, image.SourceUrl, ct);
                    image.FileName = name;
                    image.ByteSize = size;
                }
                if (image.SourceThumbUrl is not null && !store.Exists(image.ThumbFileName))
                    image.ThumbFileName = (await FetchAsync(client, image.SourceThumbUrl, ct)).Name;

                image.DownloadedAtUtc = time.GetUtcNow().UtcDateTime;
                image.DownloadError = null;
                downloaded++;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Gone)
            {
                // The photo was removed on Etsy (the next import drops the row). Retrying won't help, so don't fail the job.
                logger.LogInformation("Image {ImageId} is gone from Etsy's CDN ({Status})", image.EtsyImageId, (int)ex.StatusCode!);
                image.DownloadError = $"Etsy no longer has this image ({(int)ex.StatusCode!})";
                gone++;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException
                                           || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                logger.LogWarning("Image {ImageId} ({Url}) failed to download: {Message}", image.EtsyImageId, image.SourceUrl, ex.Message);
                image.DownloadError = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                failed++;
            }
            await db.SaveChangesAsync(ct);
        }

        return new ImageDownloadResult(downloaded, failed, pending.Count, gone);
    }

    private async Task<(string Name, long Size)> FetchAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0) throw new IOException($"Empty response from {url}");
        var ext = ImageStore.ExtensionFor(response.Content.Headers.ContentType?.MediaType, url);
        return (await store.SaveAsync(bytes, ext, ct), bytes.LongLength);
    }

    /// <summary>Deletes stored files no image row refers to any more (replaced or removed photos).</summary>
    public async Task<int> DeleteOrphansAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var names = await db.ListingImages.Select(i => new { i.FileName, i.ThumbFileName }).ToListAsync(ct);
        var referenced = names.SelectMany(n => new[] { n.FileName, n.ThumbFileName })
            .OfType<string>().ToHashSet(StringComparer.Ordinal);
        var deleted = store.DeleteUnreferenced(referenced, OrphanMinAge, time.GetUtcNow().UtcDateTime);
        if (deleted > 0) logger.LogInformation("Deleted {Count} unreferenced image file(s)", deleted);
        return deleted;
    }
}
