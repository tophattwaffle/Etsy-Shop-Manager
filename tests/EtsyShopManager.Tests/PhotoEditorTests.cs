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

    /// <summary>Etsy's photos with ranks 1..n in this order.</summary>
    private static IReadOnlyList<(long, int, string)> Etsy(params long[] ids) => [.. ids.Select((id, i) => (id, i + 1, $"Photo {id}"))];

    private static IEnumerable<string> Calls(IReadOnlyList<PhotoCall> calls) => calls.Select(c => $"{c.Kind} {c.ImageId}@{c.Rank}");

    [Fact]
    public void A_reorder_gives_every_photo_not_in_place_its_own_rank()
    {
        // Etsy doesn't renumber the others when one photo gets a rank, so every photo after the front one needs its own.
        Assert.Equal(["Rank 4@1", "Rank 1@2", "Rank 2@3", "Rank 3@4"], Calls(PhotoEditor.Plan(Etsy(1, 2, 3, 4), Photos(4, 1, 2, 3))));
        Assert.Equal(["Rank 2@1", "Rank 1@2"], Calls(PhotoEditor.Plan(Etsy(1, 2, 3), Photos(2, 1, 3))));
        Assert.Empty(PhotoEditor.Plan(Etsy(1, 2, 3), Photos(1, 2, 3)));
    }

    [Fact]
    public void Gaps_and_ties_in_Etsys_ranks_are_cleaned_up()
    {
        // Real ranks on listing 1387884604 were 1, 2, 3, 4, 6, 7; after the third test run two photos shared rank 1.
        IReadOnlyList<(long, int, string)> gaps = [(1, 1, "Photo 1"), (2, 2, "Photo 2"), (3, 3, "Photo 3"), (4, 4, "Photo 4"), (5, 6, "Photo 5"), (6, 7, "Photo 6")];
        Assert.Equal(["Rank 5@5", "Rank 6@6"], Calls(PhotoEditor.Plan(gaps, Photos(1, 2, 3, 4, 5, 6))));

        IReadOnlyList<(long, int, string)> tie = [(1, 1, "Photo 1"), (2, 1, "Photo 2"), (3, 2, "Photo 3")];
        Assert.Equal(["Rank 1@2", "Rank 3@3"], Calls(PhotoEditor.Plan(tie, Photos(2, 1, 3))));
        var (rank, ids) = PhotoEditor.Ties(tie).Single();
        Assert.Equal(1, rank);
        Assert.Equal([1L, 2L], ids);
    }

    [Fact]
    public void Deletions_go_first_and_new_photos_are_added_at_their_position()
    {
        var local = PhotoEditor.Add(PhotoEditor.Remove(Photos(1, 2, 3), 1), "abc.jpg"); // 1, 3, new
        local = PhotoEditor.Move(local, 2, 0); // new, 1, 3

        var calls = PhotoEditor.Plan(Etsy(1, 2, 3), local);

        Assert.Equal(["Delete 2@2", "Upload -1@1", "Rank 1@2"], Calls(calls)); // 3 already has rank 3
    }

    [Fact]
    public void Replacing_every_photo_uploads_before_deleting_so_the_listing_always_has_one()
    {
        var calls = PhotoEditor.Plan(Etsy(1, 2), PhotoEditor.Add([], "new.jpg"));

        Assert.Equal(["Upload -1@1", "Delete 1@1", "Delete 2@2"], Calls(calls));
    }

    [Fact]
    public void Alt_text_changes_keep_the_photo_where_it_is()
    {
        var call = Assert.Single(PhotoEditor.Plan(Etsy(1, 2), PhotoEditor.SetAltText(Photos(1, 2), 1, "Side view")));

        Assert.Equal((PhotoCallKind.AltText, 2L, 2, "Side view"), (call.Kind, call.ImageId, call.Rank, call.AltText));
    }

    [Fact]
    public void No_call_ever_overwrites()
    {
        var calls = PhotoEditor.Plan(Etsy(1, 2, 3), PhotoEditor.Add(Photos(3, 1, 2), "x.jpg"));
        Assert.All(calls.Select(c => c.Describe(42, 7)), d => Assert.DoesNotContain("overwrite=true", d));
    }
}

public class PhotoPushTests
{
    /// <summary>Listing 1 with photos 11, 12, 13 (ranks 1, 2, 3 unless given).</summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> ImportedAsync(params (long Id, int Rank)[] photos)
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        (long Id, int Rank)[] list = photos.Length > 0 ? photos : [(11, 1), (12, 2), (13, 3)];
        listing["images"] = new JsonArray([.. list.Select(p => (JsonNode)FakeEtsyShop.Image(1, p.Id, p.Rank))]);
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

    private static List<(long Id, int Rank)> EtsyRanks(FakeEtsyShop s) =>
        [.. s.ListingJson(1)["images"]!.AsArray().Select(i => ((long)i!["listing_image_id"]!, (int)i["rank"]!)).OrderBy(x => x.Item2)];

    [Fact]
    public async Task A_reorder_sets_every_rank_without_overwrite_and_finishes_with_unique_ranks()
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

        Assert.Equal(
            ["listing_image_id=13&rank=1&overwrite=false&alt_text=Photo 3", "listing_image_id=11&rank=2&overwrite=false&alt_text=Photo 1",
             "listing_image_id=12&rank=3&overwrite=false&alt_text=Photo 2"],
            shop.Writes.Select(w => w.Body));
        Assert.Equal([(13L, 1), (11L, 2), (12L, 3)], EtsyRanks(shop));
        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.SingleAsync();
        Assert.False(listing.HasDraft); // Etsy's photos now match the edit
        Assert.False(listing.HasDrift);
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
    }

    [Fact]
    public async Task Tied_ranks_on_Etsy_are_named_in_the_review_and_cleaned_up_by_the_push()
    {
        var (shop, id) = await ImportedAsync((11, 1), (12, 1), (13, 3)); // like listing 1387884604 after the third run
        using var _ = shop;
        var photos = PhotoEditor.SetAltText(await LocalPhotosAsync(shop, id), 2, "Back");
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = photos }, "tophattwaffle");

        var preview = await Editor(shop).PreviewAsync(id);
        Assert.Contains(preview.Warnings, w => w.Contains("photos #11 and #12 share position 1"));
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);

        Assert.Equal([(11L, 1), (12L, 2), (13L, 3)], EtsyRanks(shop));
        await using var db = shop.Db.CreateDbContext();
        Assert.False((await db.Listings.SingleAsync()).HasDraft);
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
        var etsyPhotos = shop.ListingJson(1)["images"]!.AsArray().OrderBy(p => (int)p!["rank"]!).ToList();
        Assert.Equal(["Front", "Close-up"], etsyPhotos.Select(p => (string?)p!["alt_text"]));
        await using var db = shop.Db.CreateDbContext();
        Assert.False((await db.Listings.SingleAsync()).HasDraft); // the upload's new Etsy id replaced the file reference
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
        // The new photo is a new image row; its files (and thumbnail for the Listings page) come with the photo download.
        Assert.Contains(shop.Provider.GetRequiredService<RecordingJobQueue>().Requests,
            r => r.JobType == EtsyShopManager.Web.Modules.Listings.DownloadListingImagesJob.Type && r.ShopId == FakeEtsyShop.ShopId);
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
        Assert.Contains("two of this listing's photos at the same position", push.Error); // Etsy's "inconsistent order", reworded
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
