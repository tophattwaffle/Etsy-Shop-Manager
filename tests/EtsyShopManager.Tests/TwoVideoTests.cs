using System.Text.Json.Nodes;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

/// <summary>
/// Listings with more than one video (Etsy's seller app allows two). Etsy keeps them in upload order with no rank, so
/// the app keeps Etsy's order, puts new videos last, and pushes removals before uploads.
/// </summary>
public class TwoVideoTests
{
    private static JsonObject Etsy(long id) => new() { ["etsy_video_id"] = id, ["video_state"] = "active" };
    private static JsonObject New(string file) => new() { ["file_name"] = file, ["name"] = file };

    [Fact]
    public async Task Import_keeps_every_video_in_Etsys_order()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Jack o lantern");
        // Etsy's order, not id order: the order buyers see.
        listing["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9101), FakeEtsyShop.Video(1, 9002));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var row = await db.Listings.Include(l => l.Videos).Include(l => l.EtsyState).SingleAsync();
        Assert.Equal([9101L, 9002L], row.Videos.OrderBy(v => v.Rank).Select(v => v.EtsyVideoId));
        var managed = JsonNode.Parse(row.EtsyState!.ManagedJson)![ListingFields.Videos]!.AsArray();
        Assert.Equal([9101L, 9002L], managed.Select(v => (long)v!["etsy_video_id"]!));
        Assert.Equal(ManagedListingState.CurrentVersion, row.EtsyState.ManagedVersion);
    }

    [Fact]
    public async Task A_second_video_added_on_Etsy_shows_as_a_change_on_Etsy()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Jack o lantern");
        listing["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9001));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        shop.ListingJson(1)["videos"]!.AsArray().Add(FakeEtsyShop.Video(1, 9002));
        shop.ListingJson(1)["last_modified_timestamp"] = 1_760_000_100;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var drift = Assert.Single(shop.Events.Events.OfType<DriftDetected>());
        Assert.Contains(ListingFields.Videos, drift.Fields);
    }

    [Fact]
    public async Task Listings_read_before_the_videos_list_keep_their_unresolved_changes_on_Etsy()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Jack o lantern");
        listing["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9001));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        // As version 6 stored it: one "video", and a title changed on Etsy that hasn't been resolved yet.
        await using (var db = shop.Db.CreateDbContext())
        {
            var state = await db.ListingEtsyStates.SingleAsync();
            var oldBase = JsonNode.Parse(state.BaseJson ?? state.ManagedJson)!.AsObject();
            oldBase.Remove(ListingFields.Videos);
            oldBase["video"] = new JsonObject { ["etsy_video_id"] = 9001, ["video_state"] = "active", ["width"] = 1920, ["height"] = 1080 };
            oldBase["title"] = "Title before the change on Etsy";
            state.BaseJson = oldBase.ToJsonString();
            state.ManagedVersion = 6;
            await db.SaveChangesAsync();
        }

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, true, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.Listings.Include(l => l.EtsyState).SingleAsync();
            Assert.Equal(ManagedListingState.CurrentVersion, row.EtsyState!.ManagedVersion);
            var model = ListingEditModel.Parse(row.EtsyState.ManagedJson, row.EtsyState.BaseJson, row.DraftJson);
            Assert.Equal(["title"], model.DriftFields);
            Assert.True(row.HasDrift);
        }
    }

    [Fact]
    public void New_videos_go_after_the_Etsy_videos_and_replacing_moves_a_video_last()
    {
        var two = new JsonArray(Etsy(1), Etsy(2));
        // Adding to one Etsy video: after it.
        Assert.Equal("[1,\"b.mp4\"]", Ids(VideoPlan.Add(new JsonArray(Etsy(1)), New("b.mp4"))));
        // Replacing the first of two: the kept one moves up, the new one goes last (where Etsy will put it).
        Assert.Equal("[2,\"b.mp4\"]", Ids(VideoPlan.Add(two, New("b.mp4"), replacing: 0)));
        // Replacing the last keeps its place.
        Assert.Equal("[1,\"b.mp4\"]", Ids(VideoPlan.Add(two, New("b.mp4"), replacing: 1)));
        Assert.Equal("[2]", Ids(VideoPlan.Remove(two, 0)));
        // A new video never sits before an Etsy one, whatever order it came in.
        Assert.Equal("[1,\"a.mp4\"]", Ids(VideoPlan.Normalize([New("a.mp4"), Etsy(1)])));
    }

    [Fact]
    public void Push_plan_removes_dropped_videos_then_uploads_new_ones()
    {
        var (deletes, uploads) = VideoPlan.Plan(new JsonArray(Etsy(1), Etsy(2)), new JsonArray(Etsy(2), New("b.mp4")));
        Assert.Equal([1L], deletes);
        Assert.Equal(["b.mp4"], uploads.Select(VideoPlan.FileName));

        // Nothing changed: nothing to do.
        var same = VideoPlan.Plan(new JsonArray(Etsy(1), Etsy(2)), new JsonArray(Etsy(1), Etsy(2)));
        Assert.Empty(same.Deletes);
        Assert.Empty(same.Uploads);
    }

    [Fact]
    public void A_push_counts_as_applied_when_Etsy_has_the_kept_videos_in_place_and_new_ones_where_the_uploads_were()
    {
        var local = new JsonArray(Etsy(2), New("b.mp4"));
        Assert.True(VideoPlan.Applied(local, new JsonArray(Etsy(2), Etsy(7))));
        // Etsy put the new one first, or the upload didn't happen, or Etsy kept a video it should have dropped.
        Assert.False(VideoPlan.Applied(local, new JsonArray(Etsy(7), Etsy(2))));
        Assert.False(VideoPlan.Applied(local, new JsonArray(Etsy(2))));
        Assert.False(VideoPlan.Applied(local, new JsonArray(Etsy(2), Etsy(2))));
    }

    [Fact]
    public async Task Pushing_a_second_video_only_uploads_it()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Jack o lantern");
        listing["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9001));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var file = await shop.VideoStore.SaveAsync("second video"u8.ToArray(), ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var listingId = await ListingIdAsync(shop);
        await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Videos] = VideoPlan.Add(new JsonArray(Etsy(9001)), New(file)),
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(listingId);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Warnings, w => w.Contains("Videos: 1 call to Etsy"));
        Assert.DoesNotContain(preview.Warnings, w => w.StartsWith("DELETE"));

        await PushAsync(shop, editor, listingId);

        Assert.DoesNotContain(shop.Writes, w => w.Method == "DELETE");
        Assert.Single(shop.Writes, w => w.Method == "POST" && w.Path.EndsWith("/videos"));
        await using var db = shop.Db.CreateDbContext();
        var row = await db.Listings.Include(l => l.Videos).SingleAsync();
        Assert.False(row.HasDraft);
        Assert.Equal(2, row.Videos.Count);
        Assert.Equal(9001, row.Videos.OrderBy(v => v.Rank).First().EtsyVideoId);
    }

    [Fact]
    public async Task Replacing_the_first_of_two_videos_deletes_it_then_uploads_the_new_one_last()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Jack o lantern");
        listing["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9001), FakeEtsyShop.Video(1, 9002));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var file = await shop.VideoStore.SaveAsync("replacement"u8.ToArray(), ".mp4");

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var listingId = await ListingIdAsync(shop);
        await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Videos] = VideoPlan.Add(new JsonArray(Etsy(9001), Etsy(9002)), New(file), replacing: 0),
        }, "tophattwaffle");
        await PushAsync(shop, editor, listingId);

        var writes = shop.Writes.Where(w => w.Path.Contains("/videos")).ToList();
        Assert.Equal(2, writes.Count);
        Assert.Equal("DELETE", writes[0].Method);
        Assert.EndsWith("/videos/9001", writes[0].Path);
        Assert.Equal("POST", writes[1].Method);

        await using var db = shop.Db.CreateDbContext();
        var row = await db.Listings.Include(l => l.Videos).SingleAsync();
        Assert.False(row.HasDraft);
        var order = row.Videos.OrderBy(v => v.Rank).Select(v => v.EtsyVideoId).ToList();
        Assert.Equal(2, order.Count);
        Assert.Equal(9002, order[0]);
    }

    [Fact]
    public async Task More_videos_than_Etsy_allows_block_the_push()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Jack o lantern"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var files = new List<string>();
        foreach (var n in new[] { "a", "b", "c" })
            files.Add(await shop.VideoStore.SaveAsync(System.Text.Encoding.UTF8.GetBytes(n), ".mp4"));

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        var listingId = await ListingIdAsync(shop);
        await editor.SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            [ListingFields.Videos] = new JsonArray([.. files.Select(f => (JsonNode)New(f))]),
        }, "tophattwaffle");

        var preview = await editor.PreviewAsync(listingId);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains($"Etsy allows {VideoPlan.MaxVideos} videos per listing"));
    }

    [Fact]
    public async Task Cloning_copies_every_video_in_order()
    {
        using var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Jack o lantern");
        listing["videos"] = new JsonArray(FakeEtsyShop.Video(1, 9101), FakeEtsyShop.Video(1, 9002));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        var source = await db.Listings.Include(l => l.Videos).Include(l => l.EtsyState).SingleAsync();
        var local = JsonNode.Parse(source.EtsyState!.ManagedJson)!.AsObject();
        var files = ListingCloner.VideoFiles(source, local);
        Assert.Equal(source.Videos.OrderBy(v => v.Rank).Select(v => v.FileName), files);
        Assert.Equal(2, files.Count);
    }

    private static string Ids(JsonArray videos) =>
        new JsonArray([.. videos.Select(v => VideoPlan.EtsyId(v!) is { } id ? JsonValue.Create(id) : (JsonNode?)JsonValue.Create(VideoPlan.FileName(v!)))]).ToJsonString();

    private static async Task<int> ListingIdAsync(FakeEtsyShop shop)
    {
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.Select(l => l.Id).SingleAsync();
    }

    private static async Task PushAsync(FakeEtsyShop shop, ListingEditor editor, int listingId)
    {
        var (pushId, blockers) = await editor.RequestPushAsync(listingId, "tophattwaffle");
        Assert.Empty(blockers);
        await shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = PushListingJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId!.Value), JobJson.Options),
        }, CancellationToken.None);
    }
}
