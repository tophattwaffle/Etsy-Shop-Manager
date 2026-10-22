using System.Net;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Infrastructure.Storage;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ChallengerM6VideoStressTests
{
    [Fact]
    public async Task Push_transitions_add_video_to_listing_with_no_video()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // 1. Initial listing has no video
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Oak Desk"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var videoBytes = "sample video payload 10101"u8.ToArray();
        var fileName = await shop.VideoStore.SaveAsync(videoBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = fileName,
                ["name"] = "desk_spin.mp4",
            },
        }, "tophattwaffle");

        // Preview checks
        var preview = await editor.PreviewAsync(1);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Changes, c => c.Key == ListingFields.Video);
        Assert.Contains(preview.Warnings, w => w.Contains("Video: 1 call to Etsy (POST"));

        // Push execution
        var (pushId, blockers) = await editor.RequestPushAsync(1, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Assertions: Exactly one POST, zero DELETE calls
        var videoPosts = shop.Writes.Where(w => w.Method == "POST" && w.Path.EndsWith("/videos")).ToList();
        var videoDeletes = shop.Writes.Where(w => w.Method == "DELETE" && w.Path.Contains("/videos")).ToList();
        Assert.Single(videoPosts);
        Assert.Empty(videoDeletes);
        Assert.Contains("desk_spin.mp4", videoPosts[0].Body);

        // Verify database and draft state
        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == 1);
        Assert.False(listing.HasDraft);
        Assert.Single(listing.Videos);
        Assert.Equal("active", listing.Videos[0].VideoState);
    }

    [Fact]
    public async Task Push_transitions_replace_existing_video_on_Etsy()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // 1. Initial listing has existing video #5501
        var listingJson = FakeEtsyShop.Listing(1, "Walnut Chair");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 5501));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var replacementBytes = "new chair video bytes"u8.ToArray();
        var replacementFile = await shop.VideoStore.SaveAsync(replacementBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = replacementFile,
                ["name"] = "chair_v2.mp4",
            },
        }, "tophattwaffle");

        // Preview checks
        var preview = await editor.PreviewAsync(1);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Warnings, w => w.Contains("Video: 2 calls to Etsy (DELETE existing video #5501, then POST to upload \"chair_v2.mp4\")"));

        // Push execution
        var (pushId, blockers) = await editor.RequestPushAsync(1, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 2,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Assertions: DELETE followed by POST
        var writes = shop.Writes.Where(w => w.Path.Contains("/videos")).ToList();
        Assert.Equal(2, writes.Count);
        Assert.Equal("DELETE", writes[0].Method);
        Assert.EndsWith("/videos/5501", writes[0].Path);
        Assert.Equal("POST", writes[1].Method);
        Assert.EndsWith("/videos", writes[1].Path);
        Assert.Contains("chair_v2.mp4", writes[1].Body);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == 1);
        Assert.False(listing.HasDraft);
    }

    [Fact]
    public async Task Push_transitions_delete_video_from_Etsy()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // 1. Initial listing has existing video #5502
        var listingJson = FakeEtsyShop.Listing(1, "Maple Bowl");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 5502));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        // Explicitly set Video to null (removal)
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = null,
        }, "tophattwaffle");

        // Preview checks
        var preview = await editor.PreviewAsync(1);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Warnings, w => w.Contains("Video: 1 call to Etsy (DELETE /v3/application/shops/42/listings/1/videos/5502)"));

        var (pushId, blockers) = await editor.RequestPushAsync(1, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 3,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Assertions: DELETE called, no POST
        var videoDeletes = shop.Writes.Where(w => w.Method == "DELETE" && w.Path.Contains("/videos")).ToList();
        var videoPosts = shop.Writes.Where(w => w.Method == "POST" && w.Path.Contains("/videos")).ToList();
        Assert.Single(videoDeletes);
        Assert.EndsWith("/videos/5502", videoDeletes[0].Path);
        Assert.Empty(videoPosts);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == 1);
        Assert.False(listing.HasDraft);
    }

    [Fact]
    public async Task Push_transitions_no_video_change_makes_zero_video_api_calls()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Listing with existing video #5503
        var listingJson = FakeEtsyShop.Listing(1, "Cedar Chest");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 5503));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Clear write log from import
        shop.Writes.Clear();

        // Edit ONLY title
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            ["title"] = "Cedar Blanket Chest - Solid Wood",
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(1);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.DoesNotContain(preview.Changes, c => c.Key == ListingFields.Video);
        Assert.DoesNotContain(preview.Warnings, w => w.Contains("Video:"));

        var (pushId, blockers) = await editor.RequestPushAsync(1, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await job.HandleAsync(new JobExecutionContext
        {
            JobId = 4,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify: updateListing was called, but ZERO video calls made
        var patchListing = shop.Writes.FirstOrDefault(w => w.Method == "PATCH" && w.Path == "/v3/application/shops/42/listings/1");
        Assert.NotNull(patchListing.Path);
        Assert.Contains("Cedar+Blanket+Chest", patchListing.Body);

        var videoWrites = shop.Writes.Where(w => w.Path.Contains("/videos")).ToList();
        Assert.Empty(videoWrites);

        // Verify existing video remains intact
        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == 1);
        Assert.False(listing.HasDraft);
        var vid = Assert.Single(listing.Videos);
        Assert.Equal(5503, vid.EtsyVideoId);
    }

    [Fact]
    public async Task Drift_detection_video_added_on_Etsy_detected_and_blocks_push()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // 1. Initial import with no video
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Teak Coaster"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // 2. Video added externally on Etsy
        shop.ListingJson(1)["videos"] = new JsonArray(FakeEtsyShop.Video(1, 6601));
        shop.ListingJson(1)["last_modified_timestamp"] = 1_770_000_100;

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Verify drift in DB & domain events
        await using (var db = shop.Db.CreateDbContext())
        {
            var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == 1);
            Assert.True(listing.HasDrift);
        }

        var driftEvent = Assert.Single(shop.Events.Events.OfType<DriftDetected>());
        Assert.Contains(ListingFields.Video, driftEvent.Fields);

        // Verify ListingEditor preview blocks push
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var preview = await editor.PreviewAsync(1);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("Changed on Etsy since the last sync: Video"));

        // Resolve drift by taking Etsy's video
        await editor.ResolveDriftAsync(1, ListingFields.Video, DriftResolution.TakeEtsy, "tophattwaffle");

        await using (var db = shop.Db.CreateDbContext())
        {
            var listing = await db.Listings.Include(l => l.Videos).SingleAsync(l => l.Id == 1);
            Assert.False(listing.HasDrift);
        }
    }

    [Fact]
    public async Task Drift_detection_video_deleted_on_Etsy_and_resolved_with_keep_mine()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // 1. Initial import with video #6602
        var listingJson = FakeEtsyShop.Listing(1, "Pine Table");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 6602));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // 2. Video removed externally on Etsy
        shop.ListingJson(1)["videos"] = new JsonArray();
        shop.ListingJson(1)["last_modified_timestamp"] = 1_770_000_200;

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            var listing = await db.Listings.SingleAsync(l => l.Id == 1);
            Assert.True(listing.HasDrift);
        }

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var preview = await editor.PreviewAsync(1);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("Changed on Etsy since the last sync: Video"));

        // Resolve drift by keeping mine (local agreed state restored to draft)
        await editor.ResolveDriftAsync(1, ListingFields.Video, DriftResolution.KeepMine, "tophattwaffle");

        await using (var db = shop.Db.CreateDbContext())
        {
            var listing = await db.Listings.SingleAsync(l => l.Id == 1);
            Assert.False(listing.HasDrift);
            // Draft should now hold the desire to have the video back
            Assert.True(listing.HasDraft);
        }
    }

    [Fact]
    public async Task ListingCloner_transfers_video_and_executes_upload_call()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Source listing with video
        var listingJson = FakeEtsyShop.Listing(1, "Ceramic Mug");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 7701));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Populate local photo files so photos don't block
        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await db.SaveChangesAsync();
        }

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(1, FakeEtsyShop.ShopId);
        choice = choice with { Title = "Ceramic Mug Clone" };

        var plan = await cloner.PlanAsync(1, choice);
        Assert.True(plan.CanClone);
        Assert.Contains(plan.Calls, c => c.StartsWith("POST") && c.EndsWith("/videos  video=") || (c.Contains("/videos") && c.Contains("video=")));

        var (cloneId, blockers) = await cloner.RequestAsync(1, choice, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(cloneId);

        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 5,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify video upload call on new draft listing
        var videoUpload = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.Contains("/listings/") && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoUpload.Path);
        Assert.NotEqual("/v3/application/shops/42/listings/1/videos", videoUpload.Path); // Uploaded to new listing ID
    }

    [Fact]
    public async Task ListingCloner_respects_draft_video_removal_on_source()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Source listing with video #7702 on Etsy
        var listingJson = FakeEtsyShop.Listing(1, "Linen Towel");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 7702));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await db.SaveChangesAsync();
        }

        // Locally remove video in draft
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = null,
        }, "tophattwaffle");

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(1, FakeEtsyShop.ShopId);
        choice = choice with { Title = "Linen Towel Clone Without Video" };

        var plan = await cloner.PlanAsync(1, choice);
        Assert.True(plan.CanClone);
        Assert.DoesNotContain(plan.Calls, c => c.Contains("/videos"));

        var (cloneId, blockers) = await cloner.RequestAsync(1, choice, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(cloneId);

        shop.Writes.Clear();

        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 6,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify NO video upload call made
        var videoUpload = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.Null(videoUpload.Path);
    }

    [Fact]
    public async Task ListingCloner_respects_draft_new_video_on_source()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Source listing with NO video on Etsy
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Wool Scarf"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await db.SaveChangesAsync();
        }

        // Locally add a video in draft
        var videoBytes = "scarf weave details"u8.ToArray();
        var videoFile = await shop.VideoStore.SaveAsync(videoBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = videoFile,
                ["name"] = "scarf_close.mp4",
            },
        }, "tophattwaffle");

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(1, FakeEtsyShop.ShopId);
        choice = choice with { Title = "Wool Scarf Clone With Draft Video" };

        var plan = await cloner.PlanAsync(1, choice);
        Assert.True(plan.CanClone);
        Assert.Contains(plan.Calls, c => c.Contains("/videos") && c.Contains(videoFile));

        var (cloneId, blockers) = await cloner.RequestAsync(1, choice, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(cloneId);

        shop.Writes.Clear();

        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 7,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify video was uploaded to the new listing
        var videoUpload = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoUpload.Path);
    }

    [Fact]
    public async Task ListingCloner_gracefully_skips_video_if_local_file_is_missing()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        // Source listing with video record whose local file does not exist
        var listingJson = FakeEtsyShop.Listing(1, "Glass Goblet");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 7703));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            // Deliberately set FileName to nonexistent
            var vid = await db.ListingVideos.FirstAsync();
            vid.FileName = "missing_file_000.mp4";
            await db.SaveChangesAsync();
        }

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(1, FakeEtsyShop.ShopId);
        choice = choice with { Title = "Glass Goblet Clone" };

        var plan = await cloner.PlanAsync(1, choice);
        Assert.True(plan.CanClone);
        // Because the file is missing in store, video call is omitted from plan
        Assert.DoesNotContain(plan.Calls, c => c.Contains("/videos"));

        var (cloneId, blockers) = await cloner.RequestAsync(1, choice, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(cloneId);

        shop.Writes.Clear();

        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        // Clone completes smoothly without crashing
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 8,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId.Value), JobJson.Options),
        }, CancellationToken.None);

        var videoUpload = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.Null(videoUpload.Path);
    }

    [Fact]
    public async Task Push_fails_and_preserves_draft_when_Etsy_rejects_video_upload()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        shop.Listings.Add(FakeEtsyShop.Listing(1, "Bronze Bell"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var videoBytes = "bronze sound"u8.ToArray();
        var fileName = await shop.VideoStore.SaveAsync(videoBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Video] = new JsonObject
            {
                ["file_name"] = fileName,
                ["name"] = "bell.mp4",
            },
        }, "tophattwaffle");

        var (pushId, blockers) = await editor.RequestPushAsync(1, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(pushId);

        // Configure Etsy to reject writes with 500
        shop.FailWritesWith = HttpStatusCode.InternalServerError;

        var job = shop.Provider.GetRequiredService<PushListingJob>();
        await Assert.ThrowsAnyAsync<EtsyApiException>(() => job.HandleAsync(new JobExecutionContext
        {
            JobId = 9,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None));

        // Draft must be preserved so user's work is not lost on network/Etsy failure
        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.SingleAsync(l => l.Id == 1);
        Assert.True(listing.HasDraft);
    }
}
