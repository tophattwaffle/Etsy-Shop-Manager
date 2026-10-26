using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

/// <summary>
/// Etsy renumbers photo ranks on its own (live shop, 2026-10-25): after deletes it closed the gap, and later it replaced
/// ties with 1 to n. Neither is a change made on Etsy, and a push must still end with the photos in the wanted order.
/// </summary>
public class PhotoDriftTests
{
    private static JsonArray Photos(params (long Id, int Rank)[] photos) =>
        new([.. photos.Select(p => (JsonNode)new JsonObject { ["etsy_image_id"] = p.Id, ["rank"] = p.Rank, ["alt_text"] = $"Photo {p.Id}" })]);

    [Fact]
    public void Rank_numbers_alone_are_not_a_change()
    {
        Assert.True(PhotoOrder.Same(Photos((1, 1), (2, 2), (3, 3)), Photos((1, 1), (2, 2), (3, 10))));
        Assert.True(PhotoOrder.Same(Photos((1, 2), (2, 5)), Photos((1, 1), (2, 2))));
    }

    [Fact]
    public void A_tie_in_the_agreed_photos_accepts_either_order_on_Etsy()
    {
        var agreed = Photos((1, 1), (2, 2), (3, 2), (4, 3)); // 2 and 3 tied, as read after the 2026-10-25 pushes
        Assert.True(PhotoOrder.Same(Photos((1, 1), (3, 2), (2, 3), (4, 4)), agreed));
        Assert.True(PhotoOrder.Same(Photos((1, 1), (2, 2), (3, 3), (4, 4)), agreed));
        Assert.False(PhotoOrder.Same(Photos((1, 1), (2, 2), (4, 3), (3, 4)), agreed)); // 4 moved before 3
    }

    [Fact]
    public void A_tie_on_Etsy_does_not_count_as_the_wanted_order()
    {
        Assert.False(PhotoOrder.Same(Photos((1, 1), (2, 1)), Photos((1, 1), (2, 2))));
    }

    [Fact]
    public void Real_photo_changes_still_count()
    {
        var etsy = Photos((1, 1), (2, 2), (3, 3));
        Assert.False(PhotoOrder.Same(etsy, Photos((2, 1), (1, 2), (3, 3))));           // order
        Assert.False(PhotoOrder.Same(etsy, Photos((1, 1), (2, 2))));                   // a photo removed
        Assert.False(PhotoOrder.Same(etsy, Photos((1, 1), (2, 2), (4, 3))));           // a different photo
        var alt = Photos((1, 1), (2, 2), (3, 3));
        alt[2]!["alt_text"] = "Back";
        Assert.False(PhotoOrder.Same(etsy, alt));                                      // alt text
    }

    private static async Task<(FakeEtsyShop Shop, int ListingId)> ImportedAsync(params (long Id, int Rank)[] photos)
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Soap sleeve");
        listing["images"] = new JsonArray([.. photos.Select(p => (JsonNode)FakeEtsyShop.Image(1, p.Id, p.Rank))]);
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    /// <summary>Etsy's photos change to these (id, rank) pairs, keeping each photo's alt text, then the shop is imported again.</summary>
    private static async Task<Listing> EtsyRenumbersAsync(FakeEtsyShop shop, params (long Id, int Rank)[] photos)
    {
        var images = shop.ListingJson(1)["images"]!.AsArray().Select(i => i!.DeepClone().AsObject()).ToList();
        foreach (var image in images) image["rank"] = photos.Single(p => p.Id == (long)image["listing_image_id"]!).Rank;
        shop.ListingJson(1)["images"] = new JsonArray([.. images.OrderBy(i => (int)i["rank"]!)]);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return await db.Listings.SingleAsync();
    }

    [Fact]
    public async Task Etsy_closing_a_gap_is_not_flagged_as_changed_on_Etsy()
    {
        var (shop, _) = await ImportedAsync((11, 1), (12, 2), (13, 10)); // like Pyramid design, listing 1559495320
        using var _ = shop;

        var listing = await EtsyRenumbersAsync(shop, (11, 1), (12, 2), (13, 3));

        Assert.False(listing.HasDrift);
    }

    [Fact]
    public async Task Etsy_settling_a_tie_is_not_flagged_as_changed_on_Etsy()
    {
        var (shop, _) = await ImportedAsync((11, 1), (12, 2), (13, 2), (14, 3)); // like Wave design, listing 1575130049
        using var _ = shop;

        var listing = await EtsyRenumbersAsync(shop, (11, 1), (13, 2), (12, 3), (14, 4));

        Assert.False(listing.HasDrift);
    }

    [Fact]
    public async Task Photos_reordered_on_Etsy_are_still_flagged()
    {
        var (shop, _) = await ImportedAsync((11, 1), (12, 2), (13, 3));
        using var _ = shop;

        var listing = await EtsyRenumbersAsync(shop, (13, 1), (11, 2), (12, 3));

        Assert.True(listing.HasDrift);
    }

    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static Task RunPushAsync(FakeEtsyShop s, int pushId) =>
        s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    /// <summary>Deletes photo 12 and adds a new photo in its place, before 13 (the template photo that stays last).</summary>
    private static async Task<int> ReplaceMiddlePhotoAsync(FakeEtsyShop shop, int id)
    {
        var file = await shop.Provider.GetRequiredService<ImageStore>().SaveAsync(new byte[2048], ".jpg");
        var photos = (JsonArray)(await Editor(shop).LoadAsync(id))!.Model.Local(ListingFields.Images)!.DeepClone();
        photos = PhotoEditor.Move(PhotoEditor.Add(PhotoEditor.Remove(photos, 1), file, "Swatch"), 2, 1); // 11, new, 13
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Images] = photos }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        return pushId!.Value;
    }

    [Fact]
    public async Task A_push_puts_photos_right_when_Etsy_renumbers_after_a_delete()
    {
        var (shop, id) = await ImportedAsync((11, 1), (12, 2), (13, 3));
        using var _ = shop;
        shop.RenumberPhotosOnDelete = true; // 13 moves to 2 when 12 goes, so the new photo uploaded at 2 ties with it
        var pushId = await ReplaceMiddlePhotoAsync(shop, id);

        await RunPushAsync(shop, pushId);

        var etsyPhotos = shop.ListingJson(1)["images"]!.AsArray().OrderBy(p => (int)p!["rank"]!)
            .Select(p => ((string?)p!["alt_text"], (int)p["rank"]!)).ToList();
        Assert.Equal([("Photo 1", 1), ("Swatch", 2), ("Photo 3", 3)], etsyPhotos);
        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.SingleAsync();
        Assert.False(listing.HasDraft);
        Assert.False(listing.HasDrift);
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_push_that_fails_after_deleting_a_photo_says_part_of_it_reached_Etsy()
    {
        var (shop, id) = await ImportedAsync((11, 1), (12, 2), (13, 3));
        using var _ = shop;
        var pushId = await ReplaceMiddlePhotoAsync(shop, id);
        shop.FailImageMovesWith = "Too many requests."; // the delete goes through, the upload is refused (2026-10-25: a 429)

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunPushAsync(shop, pushId));

        await using var db = shop.Db.CreateDbContext();
        var push = await db.ListingPushes.SingleAsync();
        Assert.Equal(ListingPushStatus.PartlyApplied, push.Status);
        Assert.Contains("Part of the push reached Etsy", push.Error);
        Assert.DoesNotContain("Nothing else changed", push.Error);
        Assert.True((await db.Listings.SingleAsync()).HasDraft);
    }
}
