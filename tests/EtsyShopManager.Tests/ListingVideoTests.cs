using System.Net;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Infrastructure.Storage;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ListingVideoTests
{
    [Fact]
    public async Task VideoStore_saves_retrieves_and_verifies_hash_naming()
    {
        using var tempStorage = new TempStorage();
        var store = new VideoStore(tempStorage.Options);

        var content = "test video stream data 12345"u8.ToArray();
        using var stream = new MemoryStream(content);
        var fileName = await store.SaveAsync(stream, ".mp4");

        Assert.EndsWith(".mp4", fileName);
        Assert.True(store.Exists(fileName));
        Assert.Equal(content.Length, new FileInfo(store.PathFor(fileName)).Length);

        var retrieved = await File.ReadAllBytesAsync(store.PathFor(fileName));
        Assert.Equal(content, retrieved);

        // Content-addressing is idempotent
        using var stream2 = new MemoryStream(content);
        var fileName2 = await store.SaveAsync(stream2, ".mp4");
        Assert.Equal(fileName, fileName2);
    }

    [Fact]
    public void VideoStore_handles_content_types_and_extensions()
    {
        Assert.Equal("video/mp4", VideoStore.ContentTypeFor("test.mp4"));
        Assert.Equal("video/quicktime", VideoStore.ContentTypeFor("test.mov"));
        Assert.Equal("application/octet-stream", VideoStore.ContentTypeFor("test.xyz"));

        Assert.Equal("mp4", VideoStore.ExtensionFor("video/mp4", "https://example.com/video"));
        Assert.Equal("mov", VideoStore.ExtensionFor("video/quicktime", "https://example.com/video"));
        Assert.Equal("mp4", VideoStore.ExtensionFor(null, "https://example.com/video.mp4"));
        Assert.Equal("mov", VideoStore.ExtensionFor(null, "https://example.com/video.mov"));
    }

    [Fact]
    public async Task VideoStore_cleans_up_unreferenced_files()
    {
        using var tempStorage = new TempStorage();
        var store = new VideoStore(tempStorage.Options);

        var file1 = await store.SaveAsync("file1"u8.ToArray(), ".mp4");
        var file2 = await store.SaveAsync("file2"u8.ToArray(), ".mp4");

        Assert.True(store.Exists(file1));
        Assert.True(store.Exists(file2));

        var deleted = store.DeleteUnreferenced(new HashSet<string> { file1 }, TimeSpan.Zero, DateTime.UtcNow);
        Assert.Equal(1, deleted);
        Assert.True(store.Exists(file1));
        Assert.False(store.Exists(file2));
    }

    [Fact]
    public async Task Import_saves_video_record_and_downloads_file()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingJson = FakeEtsyShop.Listing(1, "Vase with video");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 8801));
        shop.Listings.Add(listingJson);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync();

        var video = Assert.Single(listing.Videos);
        Assert.Equal(8801, video.EtsyVideoId);
        Assert.Equal("active", video.VideoState);
        Assert.NotNull(video.FileName);
        Assert.True(shop.VideoStore.Exists(video.FileName!));
        Assert.True(video.ByteSize > 0);
        Assert.NotNull(video.DownloadedAtUtc);
    }

    [Fact]
    public async Task Video_change_on_Etsy_triggers_drift_detection()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingJson = FakeEtsyShop.Listing(1, "Vase without video");
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // Add video on Etsy
        shop.ListingJson(1)["videos"] = new JsonArray(FakeEtsyShop.Video(1, 8802));
        shop.ListingJson(1)["last_modified_timestamp"] = 1_760_000_100;

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync();
        Assert.True(listing.HasDrift);

        var driftEvent = Assert.Single(shop.Events.Events.OfType<DriftDetected>());
        Assert.Contains(ListingFields.Videos, driftEvent.Fields);
    }

    [Fact]
    public async Task Missing_video_file_blocks_push_preview()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        shop.Listings.Add(FakeEtsyShop.Listing(1, "Vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Videos] = new JsonArray(new JsonObject
            {
                ["file_name"] = "nonexistent_video.mp4",
                ["name"] = "my_video.mp4",
            }),
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(1);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("The video file was added here but its file is gone"));
    }

    [Fact]
    public async Task Push_uploads_new_video_to_Etsy()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        shop.Listings.Add(FakeEtsyShop.Listing(1, "Vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var videoBytes = "video content for upload"u8.ToArray();
        var fileName = await shop.VideoStore.SaveAsync(videoBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Videos] = new JsonArray(new JsonObject
            {
                ["file_name"] = fileName,
                ["name"] = "demo.mp4",
            }),
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(1);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Changes, c => c.Key == ListingFields.Videos);

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

        // Verify POST .../videos was called
        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);
        Assert.Contains("demo.mp4", videoPost.Body);

        // Verify draft was cleared
        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync();
        Assert.False(listing.HasDraft);
        Assert.Single(listing.Videos);
    }

    [Fact]
    public async Task Push_replaces_existing_video_by_deleting_and_uploading()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingJson = FakeEtsyShop.Listing(1, "Vase with existing video");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9901));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var newVideoBytes = "replacement video"u8.ToArray();
        var fileName = await shop.VideoStore.SaveAsync(newVideoBytes, ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Videos] = new JsonArray(new JsonObject
            {
                ["file_name"] = fileName,
                ["name"] = "replacement.mp4",
            }),
        }, "tophattwaffle");

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

        // Verify DELETE was issued for 9901 and POST was issued for replacement
        var videoDelete = shop.Writes.FirstOrDefault(w => w.Method == "DELETE" && w.Path.EndsWith("/videos/9901"));
        Assert.NotNull(videoDelete.Path);

        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);
        Assert.Contains("replacement.mp4", videoPost.Body);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync();
        Assert.False(listing.HasDraft);
    }

    [Fact]
    public async Task Push_removes_video_by_deleting_from_Etsy()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingJson = FakeEtsyShop.Listing(1, "Vase with video to delete");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9902));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(1, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Videos] = new JsonArray(),
        }, "tophattwaffle");

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

        var videoDelete = shop.Writes.FirstOrDefault(w => w.Method == "DELETE" && w.Path.EndsWith("/videos/9902"));
        Assert.NotNull(videoDelete.Path);

        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        Assert.Null(videoPost.Path);

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Videos).SingleAsync();
        Assert.False(listing.HasDraft);
    }

    [Fact]
    public async Task ListingCloner_copies_video_to_new_draft()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;

        var listingJson = FakeEtsyShop.Listing(1, "Galaxy vase");
        listingJson["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9903));
        shop.Listings.Add(listingJson);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.ToListAsync())
                img.FileName = await imageStore.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await db.SaveChangesAsync();
        }

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(1, FakeEtsyShop.ShopId);
        choice = choice with { Title = "Galaxy vase clone" };

        var plan = await cloner.PlanAsync(1, choice);
        Assert.True(plan.CanClone);
        Assert.Contains(plan.Calls, c => c.Contains("POST") && c.Contains("/videos"));

        var (cloneId, blockers) = await cloner.RequestAsync(1, choice, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(cloneId);

        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 4,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Verify video was uploaded to the new draft listing
        var videoPost = shop.Writes.FirstOrDefault(w => w.Method == "POST" && w.Path.Contains("/listings/") && w.Path.EndsWith("/videos"));
        Assert.NotNull(videoPost.Path);
    }

    [Fact]
    public void DescribeVideo_formats_state_correctly()
    {
        Assert.Equal("No video", ListingEditor.DescribeVideo(null));

        var etsyVideo = new JsonObject
        {
            ["etsy_video_id"] = 7701,
            ["video_state"] = "active",
        };
        Assert.Equal("Etsy video #7701 (active)", ListingEditor.DescribeVideo(etsyVideo));

        var localVideo = new JsonObject
        {
            ["file_name"] = "abc123def456.mp4",
            ["name"] = "my_custom_clip.mp4",
        };
        Assert.Equal("New video (my_custom_clip.mp4)", ListingEditor.DescribeVideo(localVideo));
    }

    [Fact]
    public void Editor_player_keeps_the_video_shape_and_is_not_sized_by_a_poster()
    {
        // Etsy's video thumbnail is a 105 px square; as the poster it sized the player to a small square.
        var editor = File.ReadAllText(Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "Components", "Pages", "Listings", "VideoEditor.razor"));
        Assert.DoesNotContain("poster=", editor);
        Assert.Contains("class=\"esm-video\"", editor);
        var css = File.ReadAllText(Path.Combine(DesignRulesTests.Root, "src", "EtsyShopManager.Web", "wwwroot", "app.css"));
        Assert.Matches(@"\.esm-video \{[^}]*width: 100%[^}]*aspect-ratio: var\(--esm-video-ratio", css);

        Assert.Equal("--esm-video-ratio: 1280 / 960", EtsyShopManager.Web.Components.Pages.Listings.VideoEditor.AspectStyle(1280, 960));
        Assert.Null(EtsyShopManager.Web.Components.Pages.Listings.VideoEditor.AspectStyle(null, 720));
        Assert.Null(EtsyShopManager.Web.Components.Pages.Listings.VideoEditor.AspectStyle(0, 0));
    }

    [Fact]
    public void Editor_player_shows_the_first_frame_before_play()
    {
        Assert.Equal("/media/videos/a.mp4#t=0.001", EtsyShopManager.Web.Components.Pages.Listings.VideoEditor.PlayerUrl("/media/videos/a.mp4"));
        Assert.Equal("https://v.etsystatic.com/x.mp4#t=2", EtsyShopManager.Web.Components.Pages.Listings.VideoEditor.PlayerUrl("https://v.etsystatic.com/x.mp4#t=2"));
    }
}
