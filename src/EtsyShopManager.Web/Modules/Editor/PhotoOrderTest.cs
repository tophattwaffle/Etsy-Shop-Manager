using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>One call of a photo order test: put a photo the listing already has at a position.</summary>
public sealed record PhotoOrderStep(
    [property: JsonPropertyName("listing_image_id")] long ListingImageId,
    [property: JsonPropertyName("rank")] int Rank,
    [property: JsonPropertyName("overwrite")] bool Overwrite,
    [property: JsonPropertyName("send_alt_text")] bool SendAltText);

public sealed record PhotoOrderTestPayload(int TestId);

/// <summary>
/// The Admin photo order test (docs/PLAN.md, "Photos"): Etsy's way of reordering photos is undocumented and rejected the
/// reverted photo editor, so before the editor reorders photos TopHATTwaffle runs scripted calls on a listing that isn't public
/// and the app records exactly what Etsy does. Guards: only draft or inactive listings (checked again on Etsy right
/// before writing), the exact calls are shown first, a snapshot is taken, each call is followed by reads of the order,
/// and the run stops at the first rejection. Nothing here runs without an Admin starting it.
/// </summary>
public sealed class PhotoOrderTester(IDbContextFactory<AppDbContext> dbFactory, EtsyApiClient etsy, IJobQueue jobs, IAuditLog audit,
    TimeProvider time)
{
    public static readonly string[] AllowedStates = ["draft", "inactive"];
    public const int MaxSteps = 20;

    /// <summary>Seconds between a call and the second read of the order (Etsy can be slow to show photo changes).</summary>
    public const int StepWaitSeconds = 20;

    /// <summary>Seconds before the last read, after all steps.</summary>
    public const int FinalWaitSeconds = 120;

    public static bool IsAllowedState(string? state) => state is not null && AllowedStates.Contains(state);

    /// <summary>The listing's photos as Etsy has them now (a read).</summary>
    public async Task<List<EtsyListingImage>> ReadPhotosAsync(int listingId, CancellationToken ct = default)
    {
        var (shopId, etsyListingId) = await IdsAsync(listingId, ct);
        return [.. (await etsy.GetListingImagesAsync(etsyListingId, EtsyAuth.ForShop(shopId), ct)).OrderBy(i => i.Rank)];
    }

    /// <summary>Exactly what a run sends and reads, in order, for the review before it starts.</summary>
    public static IReadOnlyList<string> Calls(long etsyShopId, long etsyListingId, IReadOnlyList<PhotoOrderStep> steps,
        IReadOnlyDictionary<long, string?> altTexts)
    {
        var calls = new List<string>
        {
            $"GET /v3/application/listings/{etsyListingId} (check it is still draft or inactive; stop if not)",
            $"GET /v3/application/listings/{etsyListingId}/images (the order before; kept as a snapshot)",
        };
        foreach (var (step, n) in steps.Select((s, i) => (s, i + 1)))
        {
            var alt = step.SendAltText ? $", alt_text={altTexts.GetValueOrDefault(step.ListingImageId) ?? ""}" : "";
            calls.Add($"{n}. POST /v3/application/shops/{etsyShopId}/listings/{etsyListingId}/images  " +
                      $"listing_image_id={step.ListingImageId}, rank={step.Rank}, overwrite={(step.Overwrite ? "true" : "false")}{alt}" +
                      "  (stop here if Etsy rejects it)");
            calls.Add($"   GET …/images right away, and again after {StepWaitSeconds} s");
        }
        calls.Add($"GET …/images once more after {FinalWaitSeconds} s");
        return calls;
    }

    /// <summary>
    /// Steps that give every photo its own rank, 1 to n, in <paramref name="order"/> (overwrite off). The second test run
    /// showed Etsy sets the rank asked for and doesn't renumber the other photos, so ties appear; this run checks that
    /// setting every rank explicitly leaves each photo with a unique rank in the wanted order.
    /// </summary>
    public static List<PhotoOrderStep> SetAllRanks(IEnumerable<long> order) =>
        [.. order.Select((id, i) => new PhotoOrderStep(id, i + 1, Overwrite: false, SendAltText: true))];

    /// <summary>Checks the request and queues the run. Returns the test id, or why it can't run.</summary>
    public async Task<(int? TestId, string? Problem)> RequestAsync(int listingId, IReadOnlyList<PhotoOrderStep> steps, string? user,
        CancellationToken ct = default)
    {
        if (steps.Count == 0) return (null, "Add at least one step.");
        if (steps.Count > MaxSteps) return (null, $"At most {MaxSteps} steps per run (one per photo; Etsy allows 20 photos).");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var listing = await db.Listings.AsNoTracking().FirstOrDefaultAsync(l => l.Id == listingId, ct);
        if (listing is null) return (null, "Listing not found.");
        if (!IsAllowedState(listing.State) || listing.IsMissingOnEtsy)
            return (null, $"Only a draft or inactive listing can be used (this one is {listing.State}).");
        if (await db.PhotoOrderTests.AnyAsync(t => (t.Status == PhotoOrderTestStatus.Pending || t.Status == PhotoOrderTestStatus.Running)
                && db.Jobs.Any(j => j.Id == t.JobId && (j.Status == JobStatus.Pending || j.Status == JobStatus.Running)), ct))
            return (null, "Another photo order test is still running.");

        var test = new PhotoOrderTest
        {
            ListingId = listingId,
            Status = PhotoOrderTestStatus.Pending,
            RequestedBy = user,
            RequestedAtUtc = time.GetUtcNow().UtcDateTime,
            StepsJson = JsonSerializer.Serialize(steps),
        };
        db.PhotoOrderTests.Add(test);
        await db.SaveChangesAsync(ct);
        test.JobId = await jobs.EnqueueAsync(new JobRequest
        {
            JobType = PhotoOrderTestJob.Type,
            ShopId = listing.ShopId,
            Payload = new PhotoOrderTestPayload(test.Id),
            DedupeKey = PhotoOrderTestJob.Type,
            Priority = 20,
            MaxAttempts = 1, // a write experiment is never repeated on its own
            CreatedBy = user,
        }, ct);
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync("photos.order-test-requested", nameof(Listing), listing.EtsyListingId.ToString(),
            $"{steps.Count} step(s)", user, ct);
        return (test.Id, null);
    }

    private async Task<(int ShopId, long EtsyListingId)> IdsAsync(int listingId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var l = await db.Listings.AsNoTracking().Where(x => x.Id == listingId).Select(x => new { x.ShopId, x.EtsyListingId })
            .FirstOrDefaultAsync(ct) ?? throw new InvalidOperationException($"Listing {listingId} not found.");
        return (l.ShopId, l.EtsyListingId);
    }
}

/// <summary>Runs a photo order test (see <see cref="PhotoOrderTester"/>).</summary>
public sealed class PhotoOrderTestJob(IDbContextFactory<AppDbContext> dbFactory, EtsyApiClient etsy, IAuditLog audit, TimeProvider time,
    ILogger<PhotoOrderTestJob> logger) : IJobHandler
{
    public const string Type = "etsy.photos.order-test";
    public string JobType => Type;
    public string DisplayName => "Photo order test";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var testId = context.GetPayload<PhotoOrderTestPayload>()?.TestId ?? throw new JobPermanentFailureException("TestId is required.");
        PhotoOrderTest test;
        Listing listing;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            test = await db.PhotoOrderTests.FirstOrDefaultAsync(t => t.Id == testId, ct)
                   ?? throw new JobPermanentFailureException($"Photo order test {testId} not found.");
            if (test.Status == PhotoOrderTestStatus.Running)
            {
                // A restart interrupted it part way; never resume a write experiment on its own.
                await FinishAsync(testId, PhotoOrderTestStatus.Stopped, JsonNode.Parse(test.ResultJson ?? "{}")!.AsObject(),
                    "Stopped: interrupted by a restart. Check the photo order on Etsy before starting a new test.", test.SnapshotId);
                throw new JobPermanentFailureException($"Photo order test {testId} was interrupted by a restart.");
            }
            if (test.Status != PhotoOrderTestStatus.Pending)
                throw new JobPermanentFailureException($"Photo order test {testId} already ran ({test.Status}); start a new one.");
            listing = await db.Listings.AsNoTracking().Include(l => l.Shop).Include(l => l.EtsyState).FirstAsync(l => l.Id == test.ListingId, ct);
            test.Status = PhotoOrderTestStatus.Running;
            test.StartedAtUtc = time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
        }

        try
        {
            await RunAsync(context, testId, test, listing, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Anything unexpected (network, token): record where it got to and never retry on its own.
            await StopIfRunningAsync(testId, $"Stopped: {ex.Message} Check the photo order on Etsy before starting a new test.");
            throw ex is JobPermanentFailureException ? ex : new JobPermanentFailureException(ex.Message, ex);
        }
    }

    private async Task RunAsync(JobExecutionContext context, int testId, PhotoOrderTest test, Listing listing, CancellationToken ct)
    {
        var steps = JsonSerializer.Deserialize<List<PhotoOrderStep>>(test.StepsJson) ?? [];
        var auth = EtsyAuth.ForShop(listing.ShopId);
        var result = new JsonObject { ["listing_id"] = listing.EtsyListingId, ["steps"] = new JsonArray() };

        // Guard 1: the listing must still be a draft or inactive on Etsy right now. No write before this check.
        var fresh = await etsy.GetListingAsync(listing.EtsyListingId, auth, ct);
        result["state"] = fresh.Value.State;
        if (!PhotoOrderTester.IsAllowedState(fresh.Value.State))
        {
            await FinishAsync(testId, PhotoOrderTestStatus.Refused, result,
                $"Not run: the listing is {fresh.Value.State} on Etsy; only a draft or inactive listing can be used.", null);
            context.Result = "refused: listing is not draft or inactive";
            return;
        }

        // Guard 2: snapshot the photos before anything changes.
        var before = await ReadOrderAsync(listing.EtsyListingId, auth, ct);
        result["before"] = Ids(before);
        var snapshotId = await SnapshotAsync(listing, fresh.Json, ct);
        var order = before.Select(i => i.ListingImageId).ToList();

        foreach (var (step, n) in steps.Select((s, i) => (s, i + 1)))
        {
            var entry = new JsonObject
            {
                ["step"] = n,
                ["request"] = $"listing_image_id={step.ListingImageId}, rank={step.Rank}, overwrite={(step.Overwrite ? "true" : "false")}" +
                              (step.SendAltText ? ", alt_text=(current)" : ""),
            };
            ((JsonArray)result["steps"]!).Add(entry);
            if (!order.Contains(step.ListingImageId))
            {
                entry["answer"] = "not sent: the listing has no such photo now";
                await FinishAsync(testId, PhotoOrderTestStatus.Stopped, result, $"Stopped at step {n}: photo {step.ListingImageId} isn't on the listing.", snapshotId);
                context.Result = $"stopped at step {n}";
                return;
            }
            entry["expected"] = $"photo {step.ListingImageId} has rank {step.Rank}";
            var alt = step.SendAltText ? before.FirstOrDefault(i => i.ListingImageId == step.ListingImageId)?.AltText ?? "" : null;

            try
            {
                logger.LogInformation("Photo order test {TestId} step {Step}: image {ImageId} to rank {Rank} (overwrite {Overwrite})",
                    testId, n, step.ListingImageId, step.Rank, step.Overwrite);
                var answer = await etsy.RepositionListingImageAsync(listing.Shop.EtsyShopId, listing.EtsyListingId, step.ListingImageId,
                    step.Rank, step.Overwrite, alt, auth, ct);
                entry["answer"] = $"OK: image {answer.ListingImageId} at rank {answer.Rank}";
            }
            catch (EtsyApiException ex)
            {
                // Guard 3: stop at the first rejection, read the order once so we know what Etsy left behind.
                entry["answer"] = $"{(int)ex.StatusCode}: {ex.Error ?? ex.ResponseBody}";
                entry["read_after_rejection"] = Ids(await ReadOrderAsync(listing.EtsyListingId, auth, ct));
                await FinishAsync(testId, PhotoOrderTestStatus.Stopped, result,
                    $"Stopped at step {n}: Etsy answered {(int)ex.StatusCode} ({ex.Error ?? "see the result"}).", snapshotId);
                context.Result = $"stopped at step {n}: Etsy {(int)ex.StatusCode}";
                return;
            }

            var now = Ids(await ReadOrderAsync(listing.EtsyListingId, auth, ct));
            await Task.Delay(TimeSpan.FromSeconds(PhotoOrderTester.StepWaitSeconds), time, ct);
            var later = await ReadOrderAsync(listing.EtsyListingId, auth, ct);
            entry["read_now"] = now;
            entry[$"read_after_{PhotoOrderTester.StepWaitSeconds}s"] = Ids(later);
            entry["as_expected"] = later.Any(i => i.ListingImageId == step.ListingImageId && i.Rank == step.Rank);
            order = later.Select(i => i.ListingImageId).ToList();
            await SaveProgressAsync(testId, result, ct);
        }

        await Task.Delay(TimeSpan.FromSeconds(PhotoOrderTester.FinalWaitSeconds), time, ct);
        var final = await ReadOrderAsync(listing.EtsyListingId, auth, ct);
        result[$"final_after_{PhotoOrderTester.FinalWaitSeconds}s"] = Ids(final);
        var unique = final.Select(i => i.Rank).Distinct().Count() == final.Count;
        result["ranks_unique"] = unique;
        var matched = ((JsonArray)result["steps"]!).Count(s => (bool?)s!["as_expected"] == true);
        await FinishAsync(testId, PhotoOrderTestStatus.Completed, result,
            $"Completed {steps.Count} step(s); {matched} left the photo at the rank asked for; " +
            (unique ? "every photo has its own rank at the end." : "some photos share a rank at the end."), snapshotId);
        await audit.WriteAsync("photos.order-test", nameof(Listing), listing.EtsyListingId.ToString(), $"{steps.Count} step(s), {matched} as expected", "system", ct);
        context.Result = $"{steps.Count} step(s), {matched} as expected";
    }

    // In the order Etsy returns them (not re-sorted), each with its rank, so ties and gaps in the ranks show.
    private async Task<List<EtsyListingImage>> ReadOrderAsync(long etsyListingId, EtsyAuth auth, CancellationToken ct) =>
        await etsy.GetListingImagesAsync(etsyListingId, auth, ct);

    private static JsonArray Ids(IEnumerable<EtsyListingImage> images) =>
        new([.. images.Select(i => (JsonNode)new JsonObject { ["id"] = i.ListingImageId, ["rank"] = i.Rank })]);

    private async Task<int> SnapshotAsync(Listing listing, string rawListingJson, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var snapshot = new ListingSnapshot
        {
            ListingId = listing.Id,
            TakenAtUtc = time.GetUtcNow().UtcDateTime,
            Reason = "Before photo order test",
            ManagedJson = listing.EtsyState?.ManagedJson ?? "",
            RawListingJson = rawListingJson, // includes the photos in their order
            RawInventoryJson = "",
        };
        db.ListingSnapshots.Add(snapshot);
        await db.SaveChangesAsync(ct);
        return snapshot.Id;
    }

    private async Task SaveProgressAsync(int testId, JsonObject result, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.PhotoOrderTests.Where(t => t.Id == testId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ResultJson, result.ToJsonString()), ct);
    }

    private async Task StopIfRunningAsync(int testId, string outcome)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var now = time.GetUtcNow().UtcDateTime;
        await db.PhotoOrderTests.Where(t => t.Id == testId && t.Status == PhotoOrderTestStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, PhotoOrderTestStatus.Stopped)
            .SetProperty(t => t.FinishedAtUtc, now)
            .SetProperty(t => t.Outcome, outcome), CancellationToken.None);
    }

    private async Task FinishAsync(int testId, PhotoOrderTestStatus status, JsonObject result, string outcome, int? snapshotId)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var now = time.GetUtcNow().UtcDateTime;
        var json = result.ToJsonString();
        await db.PhotoOrderTests.Where(t => t.Id == testId).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.Status, status)
            .SetProperty(t => t.FinishedAtUtc, now)
            .SetProperty(t => t.ResultJson, json)
            .SetProperty(t => t.Outcome, outcome)
            .SetProperty(t => t.SnapshotId, snapshotId), CancellationToken.None);
    }
}
