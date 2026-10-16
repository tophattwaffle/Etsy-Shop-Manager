using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Listings;

public sealed record ImportPayload(bool Force = false);

/// <summary>Enqueues the import jobs. Dedupe keys make repeated clicks and overlapping schedules harmless.</summary>
public sealed class ListingImportQueue(IJobQueue queue)
{
    public Task<long?> ImportShopAsync(int shopId, bool force, string? createdBy, string? correlationId = null,
        CancellationToken ct = default) =>
        queue.EnqueueAsync(new JobRequest
        {
            JobType = ImportShopListingsJob.Type,
            ShopId = shopId,
            Payload = new ImportPayload(force),
            DedupeKey = $"{ImportShopListingsJob.Type}:{shopId}",
            CorrelationId = correlationId,
            CreatedBy = createdBy,
        }, ct);

    public Task<long?> ImportLookupsAsync(int shopId, string? createdBy, CancellationToken ct = default) =>
        queue.EnqueueAsync(new JobRequest
        {
            JobType = ImportShopLookupsJob.Type,
            ShopId = shopId,
            DedupeKey = $"{ImportShopLookupsJob.Type}:{shopId}",
            Priority = 5,
            CreatedBy = createdBy,
        }, ct);

    public Task<long?> DownloadImagesAsync(int shopId, string? createdBy, string? correlationId = null,
        CancellationToken ct = default) =>
        queue.EnqueueAsync(new JobRequest
        {
            JobType = DownloadListingImagesJob.Type,
            ShopId = shopId,
            DedupeKey = $"{DownloadListingImagesJob.Type}:{shopId}",
            CorrelationId = correlationId,
            Priority = -10, // after Etsy API work: it doesn't use the API quota and can take a while
            CreatedBy = createdBy,
        }, ct);

    public Task<long?> RefreshTaxonomyAsync(bool force, string? createdBy, string? correlationId = null,
        CancellationToken ct = default) =>
        queue.EnqueueAsync(new JobRequest
        {
            JobType = RefreshTaxonomyJob.Type,
            Payload = new ImportPayload(force),
            DedupeKey = RefreshTaxonomyJob.Type,
            CorrelationId = correlationId,
            CreatedBy = createdBy,
        }, ct);
}

/// <summary>
/// Full listing import for one shop (lookups, every listing state, images metadata, inventory, attributes).
/// Afterwards it queues the image download and the taxonomy refresh.
/// </summary>
public sealed class ImportShopListingsJob(ListingImporter importer, ListingImportQueue queue) : IJobHandler
{
    public const string Type = "etsy.listings.import";
    public string JobType => Type;
    public string DisplayName => "Listing import";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var shopId = context.ShopId ?? throw new JobPermanentFailureException("ShopId is required.");
        var force = context.GetPayload<ImportPayload>()?.Force ?? false;
        try
        {
            var result = await importer.ImportShopAsync(shopId, force, ct);
            context.Result = $"{result.ListingsSeen} listings" + (result.MarkedMissing > 0 ? $", {result.MarkedMissing} gone from Etsy" : "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not RetryLaterException)
        {
            await importer.RecordErrorAsync(shopId, ex.Message, CancellationToken.None);
            throw;
        }

        var correlation = context.CorrelationId ?? $"job:{context.JobId}";
        await queue.DownloadImagesAsync(shopId, "import", correlation, ct);
        await queue.RefreshTaxonomyAsync(force: false, "import", correlation, ct);
    }
}

/// <summary>Refreshes just the shop's lookups (shipping/processing/return profiles, sections, partners).</summary>
public sealed class ImportShopLookupsJob(ListingImporter importer, IDbContextFactory<AppDbContext> dbFactory) : IJobHandler
{
    public const string Type = "etsy.lookups.import";
    public string JobType => Type;
    public string DisplayName => "Lookup refresh";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var shopId = context.ShopId ?? throw new JobPermanentFailureException("ShopId is required.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var etsyShopId = await db.Shops.Where(s => s.Id == shopId).Select(s => (long?)s.EtsyShopId).FirstOrDefaultAsync(ct)
                         ?? throw new JobPermanentFailureException($"Shop {shopId} does not exist.");
        await importer.ImportLookupsAsync(shopId, etsyShopId, ct);
    }
}

public sealed class DownloadListingImagesJob(ImageDownloader downloader) : IJobHandler
{
    public const string Type = "etsy.images.download";
    public string JobType => Type;
    public string DisplayName => "Photo download";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var shopId = context.ShopId ?? throw new JobPermanentFailureException("ShopId is required.");
        var result = await downloader.DownloadForShopAsync(shopId, ct);
        await downloader.DeleteOrphansAsync(ct);
        context.Result = result.Pending == 0 ? "all photos stored"
            : $"{result.Downloaded} downloaded" + (result.Gone > 0 ? $", {result.Gone} gone from Etsy" : "")
              + (result.Failed > 0 ? $", {result.Failed} failed" : "");
        if (result.Failed > 0)
            throw new InvalidOperationException($"{result.Failed} of {result.Pending} image(s) failed to download; will retry.");
    }
}

public sealed class RefreshTaxonomyJob(TaxonomyImporter importer) : IJobHandler
{
    public const string Type = "etsy.taxonomy.refresh";
    public string JobType => Type;
    public string DisplayName => "Taxonomy refresh";

    public Task HandleAsync(JobExecutionContext context, CancellationToken ct) =>
        importer.RefreshAsync(context.GetPayload<ImportPayload>()?.Force ?? false, ct);
}

/// <summary>The nightly pull: one import job per connected shop.</summary>
public sealed class ImportAllShopsJob(IDbContextFactory<AppDbContext> dbFactory, ListingImportQueue queue) : IJobHandler
{
    public const string Type = "etsy.listings.import-all";
    public string JobType => Type;
    public string DisplayName => "Import listings (all shops)";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shopIds = await db.Shops.Where(s => s.IsActive && s.Token != null && !s.Token.NeedsReauth)
            .Select(s => s.Id).ToListAsync(ct);
        foreach (var shopId in shopIds)
            await queue.ImportShopAsync(shopId, force: false, "scheduler", $"job:{context.JobId}", ct);
        context.Result = $"{shopIds.Count} shop(s) queued";
    }
}

/// <summary>A newly connected (or reconnected) shop gets imported right away.</summary>
public sealed class ImportOnShopConnected(ListingImportQueue queue) : IDomainEventHandler<ShopConnected>
{
    public Task HandleAsync(ShopConnected e, CancellationToken ct) =>
        queue.ImportShopAsync(e.ShopId, force: false, "shop-connected", ct: ct);
}
