using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class PhotoEditorTests
{
    private static JsonArray Photos(params long[] ids) =>
        new([.. ids.Select((id, i) => (JsonNode)new JsonObject { ["etsy_image_id"] = id, ["rank"] = i + 1, ["alt_text"] = $"Photo {id}" })]);

    private static IReadOnlyList<(long, string)> Etsy(params long[] ids) => [.. ids.Select(id => (id, $"Photo {id}"))];

    private static IEnumerable<string> Calls(IReadOnlyList<PhotoCall> calls) => calls.Select(c => $"{c.Kind} {c.ImageId}@{c.Rank}");

    [Fact]
    public void A_reorder_moves_photos_into_place_from_the_front_and_skips_the_ones_already_there()
    {
        var calls = PhotoEditor.Plan(Etsy(1, 2, 3, 4), Photos(4, 1, 2, 3));
        Assert.Equal(["Move 4@1"], Calls(calls)); // one move: the others shift down by themselves

        calls = PhotoEditor.Plan(Etsy(1, 2, 3), Photos(3, 2, 1));
        Assert.Equal(["Move 3@1", "Move 2@2"], Calls(calls)); // then 1 is already last
        Assert.Empty(PhotoEditor.Plan(Etsy(1, 2, 3), Photos(1, 2, 3)));
    }

    [Fact]
    public void Deletions_go_first_new_photos_are_added_at_the_end_then_moved_into_place()
    {
        var local = PhotoEditor.Add(PhotoEditor.Remove(Photos(1, 2, 3), 1), "abc.jpg"); // 1, 3, new
        local = PhotoEditor.Move(local, 2, 0); // new, 1, 3

        var calls = PhotoEditor.Plan(Etsy(1, 2, 3), local);

        Assert.Equal(["Delete 2@2", "Upload -1@3", "Move -1@1"], Calls(calls));
    }

    [Fact]
    public void Replacing_every_photo_uploads_before_deleting_so_the_listing_always_has_one()
    {
        var calls = PhotoEditor.Plan(Etsy(1, 2), PhotoEditor.Add([], "new.jpg"));

        Assert.Equal(["Upload -1@3", "Delete 1@1", "Delete 2@1"], Calls(calls)); // then it is the only photo, already first
    }

    [Fact]
    public void Alt_text_changes_keep_the_photo_where_it_is()
    {
        var calls = PhotoEditor.Plan(Etsy(1, 2), PhotoEditor.SetAltText(Photos(1, 2), 1, "Side view"));

        var call = Assert.Single(calls);
        Assert.Equal((PhotoCallKind.AltText, 2L, 2, "Side view"), (call.Kind, call.ImageId, call.Rank, call.AltText));
    }

    [Fact]
    public void No_call_ever_overwrites()
    {
        var calls = PhotoEditor.Plan(Etsy(1, 2, 3), Photos(3, 1, 2));
        Assert.All(calls.Select(c => c.Describe(42, 7)), d => Assert.DoesNotContain("overwrite=true", d));
    }
}

public class PhotoPushTests
{
    /// <summary>Listing 1 with photos 11, 12, 13.</summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> ImportedAsync(string state = "active", params long[] photos)
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase", state: state);
        var ids = photos.Length > 0 ? photos : [11, 12, 13];
        listing["images"] = new JsonArray([.. ids.Select((id, i) => (JsonNode)FakeEtsyShop.Image(1, id, i + 1))]);
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<JsonArray> LocalPhotosAsync(FakeEtsyShop s, int id) =>
        (JsonArray)(await Editor(s).LoadAsync(id))!.Model.Local(ListingFields.Images)!.DeepClone();

    private static Task RunPushAsync(FakeEtsyShop s, int pushId) =>
        s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    private static List<long> EtsyOrder(FakeEtsyShop s) =>
        [.. s.ListingJson(1)["images"]!.AsArray().Select(i => (long)i!["listing_image_id"]!)];

    [Fact]
    public async Task The_fake_replays_Etsys_recorded_answers_to_both_photo_order_test_runs()
    {
        // TopHATTwaffle's runs on 2026-10-17 (listing 1387884604). Run 1: overwrite off moves the photo; Run 2: overwrite on
        // replaces the photo at that rank. Both: reads show the change only after ~2 minutes.
        var (shop, id) = await ImportedAsync("draft", 3046382746, 3046382084, 3094110891, 4640710505, 4640710463, 4592467744);
        using var _ = shop;
        var tester = shop.Provider.GetRequiredService<PhotoOrderTester>();
        var job = shop.Provider.GetRequiredService<PhotoOrderTestJob>();

        async Task<JsonNode> RunAsync(PhotoOrderStep step)
        {
            var (testId, problem) = await tester.RequestAsync(id, [step], "tophattwaffle");
            Assert.Null(problem);
            await job.HandleAsync(new JobExecutionContext
            {
                JobId = testId!.Value, JobType = PhotoOrderTestJob.Type, Attempt = 1,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PhotoOrderTestPayload(testId.Value), JobJson.Options),
            }, CancellationToken.None);
            shop.Provider.GetRequiredService<RecordingJobQueue>().Requests.Clear();
            await using var db = shop.Db.CreateDbContext();
            return JsonNode.Parse((await db.PhotoOrderTests.SingleAsync(t => t.Id == testId)).ResultJson!)!;
        }

        var run1 = await RunAsync(new PhotoOrderStep(4592467744, 1, Overwrite: false, SendAltText: true));
        Assert.Equal("[3046382746,3046382084,3094110891,4640710505,4640710463,4592467744]", run1["steps"]![0]!["read_now"]!.ToJsonString());
        Assert.Equal("[3046382746,3046382084,3094110891,4640710505,4640710463,4592467744]", run1["steps"]![0]!["read_after_20s"]!.ToJsonString());
        Assert.Equal("[4592467744,3046382746,3046382084,3094110891,4640710505,4640710463]", run1["final_after_120s"]!.ToJsonString());

        var run2 = await RunAsync(new PhotoOrderStep(4640710463, 1, Overwrite: true, SendAltText: true));
        Assert.Equal("[4592467744,3046382746,3046382084,3094110891,4640710505,4640710463]", run2["before"]!.ToJsonString());
        Assert.Equal("[4640710463,3046382746,3046382084,3094110891,4640710505]", run2["final_after_120s"]!.ToJsonString()); // 4592467744 is gone
    }

    [Fact]
    public async Task A_reorder_is_pushed_as_moves_without_overwrite_and_checked_after_Etsy_catches_up()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        var photos = PhotoEditor.Move(await LocalPhotosAsync(shop, id), 2, 0); // 13, 11, 12
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = photos }, "tophattwaffle");

        var preview = await Editor(shop).PreviewAsync(id);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Warnings, w => w.Contains("listing_image_id=13, rank=1, overwrite=false"));
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);

        var write = Assert.Single(shop.Writes);
        Assert.Equal(("POST", "listing_image_id=13&rank=1&overwrite=false&alt_text=Photo 3"), (write.Method, write.Body));
        Assert.Equal([13L, 11L, 12L], EtsyOrder(shop));
        Assert.Contains(PhotoEditor.SettleTime, shop.Time.Delays); // waited before reading back
        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.SingleAsync();
        Assert.False(listing.HasDraft); // Etsy's order, once visible, matched the edit
        Assert.False(listing.HasDrift);
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
    }

    [Fact]
    public async Task Adding_deleting_and_alt_text_are_pushed_and_the_new_photo_is_adopted()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        var file = await shop.Provider.GetRequiredService<ImageStore>().SaveAsync(new byte[2048], ".jpg");
        var photos = await LocalPhotosAsync(shop, id);
        photos = PhotoEditor.Remove(PhotoEditor.Remove(photos, 2), 1);      // keep 11
        photos = PhotoEditor.SetAltText(PhotoEditor.Add(photos, file, "Close-up"), 0, "Front"); // 11 (Front), new (Close-up)
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = photos }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");

        await RunPushAsync(shop, pushId!.Value);

        Assert.Equal(
            ["DELETE /v3/application/shops/42/listings/1/images/12", "DELETE /v3/application/shops/42/listings/1/images/13",
             $"POST image=<file {file}, 2048 bytes>&rank=2&overwrite=false&alt_text=Close-up",
             "POST listing_image_id=11&rank=1&overwrite=false&alt_text=Front"],
            shop.Writes.Select(w => w.Method == "DELETE" ? $"DELETE {w.Path}" : $"POST {w.Body}"));
        var etsyPhotos = shop.ListingJson(1)["images"]!.AsArray();
        Assert.Equal(2, etsyPhotos.Count);
        Assert.Equal(["Front", "Close-up"], etsyPhotos.Select(p => (string?)p!["alt_text"]));
        await using var db = shop.Db.CreateDbContext();
        Assert.False((await db.Listings.SingleAsync()).HasDraft); // the upload's new Etsy id replaced the file reference
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_rejected_photo_call_stops_the_push()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        var photos = PhotoEditor.Move(PhotoEditor.Move(await LocalPhotosAsync(shop, id), 2, 0), 2, 1); // 13, 12, 11
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = photos }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        shop.FailImageMovesWith = "The ListingImages for this Listing are in an inconsistent order.";

        await Assert.ThrowsAsync<EtsyShopManager.Etsy.EtsyApiException>(() => RunPushAsync(shop, pushId!.Value));

        Assert.Single(shop.Writes); // nothing after the first rejection
        await using var db = shop.Db.CreateDbContext();
        var push = await db.ListingPushes.SingleAsync();
        Assert.Equal(ListingPushStatus.Failed, push.Status);
        Assert.Contains("inconsistent order", push.Error);
        Assert.True((await db.Listings.SingleAsync()).HasDraft);
    }

    [Fact]
    public async Task The_preview_blocks_no_photos_and_a_lost_upload()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = new JsonArray() }, "tophattwaffle");
        Assert.Contains((await Editor(shop).PreviewAsync(id)).Blockers, b => b.Contains("at least one photo"));

        var photos = PhotoEditor.Add(await LocalPhotosAsync(shop, id), "0000000000000000000000000000000000000000000000000000000000000000.jpg");
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = photos }, "tophattwaffle");
        Assert.Contains((await Editor(shop).PreviewAsync(id)).Blockers, b => b.Contains("file is gone"));
    }
}
