using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class PhotoTemplateTests
{
    private const string SwatchAlt = "Color swatch chart of all available filament colors";

    /// <summary>Listing 1 with the given photos (id, rank, alt text) on Etsy.</summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync(params (long Id, int Rank, string Alt)[] photos)
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        if (photos.Length > 0)
            listing["images"] = new JsonArray([.. photos.Select(p =>
            {
                var image = FakeEtsyShop.Image(1, p.Id, p.Rank);
                image["alt_text"] = p.Alt;
                return (JsonNode)image;
            })]);
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static PhotoTemplateService Templates(FakeEtsyShop s) => s.Provider.GetRequiredService<PhotoTemplateService>();
    private static Task<string> FileAsync(FakeEtsyShop s, byte seed) => s.Provider.GetRequiredService<ImageStore>().SaveAsync([9, 9, seed], ".jpg");

    private static async Task<int> TemplateAsync(FakeEtsyShop s, string name = "Swatch", string alt = SwatchAlt, int rankFromEnd = 1, byte seed = 1)
    {
        var (id, error) = await Templates(s).SaveAsync(null, name, await FileAsync(s, seed), alt, rankFromEnd, "tophattwaffle");
        Assert.Null(error);
        return id!.Value;
    }

    /// <summary>Apply, then review and push through the normal push job, then the push-finished event (as the app's event bus would).</summary>
    private static async Task ApplyAndPushAsync(FakeEtsyShop s, int templateId)
    {
        Assert.Equal(1, await Templates(s).ApplyAsync(templateId, "tophattwaffle"));
        var item = Assert.Single(await Templates(s).ReviewAsync(templateId));
        Assert.True(item.Preview.CanPush, string.Join("; ", item.Preview.Blockers));
        s.Provider.GetRequiredService<RecordingJobQueue>().Requests.Clear(); // earlier pushes finished (their dedupe keys are free)
        var pushId = Assert.Single(await Templates(s).PushAsync(templateId, "tophattwaffle")).PushId!.Value;
        await s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = pushId, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);
        var handler = new PhotoTemplatesOnListingPushed(Templates(s));
        await handler.HandleAsync(s.Events.Events.OfType<ListingPushed>().Last(), CancellationToken.None);
    }

    private static IEnumerable<string> EtsyOrder(FakeEtsyShop s) =>
        s.ListingJson(1)["images"]!.AsArray().OrderBy(i => (int)i!["rank"]!).Select(i => (string)i!["alt_text"]!);

    [Fact]
    public async Task A_template_is_added_last_then_replaced_with_overwrite_off_and_unique_ranks()
    {
        var (shop, id) = await SetupAsync((11, 1, "Front"), (12, 2, "Side"));
        using var _ = shop;
        var templateId = await TemplateAsync(shop);
        Assert.False(Assert.Single(await Templates(shop).CandidatesAsync(templateId)).HasPhoto);
        await Templates(shop).LinkAsync(templateId, [id], "tophattwaffle");
        Assert.Equal((PhotoTemplateUseStatus.HasChanges, "Adds the photo"), Assert.Single(await Templates(shop).UsesAsync(templateId)) is var u ? (u.Status, u.Change) : default);

        await ApplyAndPushAsync(shop, templateId);
        var upload = Assert.Single(shop.Writes);
        Assert.Contains("rank=3&overwrite=false&alt_text=" + SwatchAlt, upload.Body);
        Assert.Equal(["Front", "Side", SwatchAlt], EtsyOrder(shop));
        Assert.Equal(PhotoTemplateUseStatus.UpToDate, Assert.Single(await Templates(shop).UsesAsync(templateId)).Status);

        // A new image for the template replaces the listing's copy: the old photo is deleted, the new one uploaded last.
        shop.Writes.Clear();
        var template = (await Templates(shop).GetAsync(templateId))!;
        await Templates(shop).SaveAsync(templateId, template.Name, await FileAsync(shop, 2), SwatchAlt, 1, "tophattwaffle");
        Assert.Equal("Replaces the photo with the template's new image", Assert.Single(await Templates(shop).UsesAsync(templateId)).Change);
        await ApplyAndPushAsync(shop, templateId);
        Assert.Equal(["DELETE", "POST"], shop.Writes.Select(w => w.Method));
        Assert.All(shop.Writes.Where(w => w.Method == "POST"), w => Assert.Contains("overwrite=false", w.Body));
        Assert.Equal(["Front", "Side", SwatchAlt], EtsyOrder(shop));
        Assert.Equal([1, 2, 3], shop.ListingJson(1)["images"]!.AsArray().Select(i => (int)i!["rank"]!).Order());
        await using var db = shop.Db.CreateDbContext();
        Assert.False((await db.Listings.SingleAsync()).HasDraft);
        Assert.Equal(PhotoTemplateUseStatus.UpToDate, Assert.Single(await Templates(shop).UsesAsync(templateId)).Status);
    }

    [Fact]
    public async Task An_existing_photo_found_by_alt_text_is_kept_and_only_moved_into_place()
    {
        var (shop, id) = await SetupAsync((11, 1, SwatchAlt), (12, 2, "Front"), (13, 3, "Side"));
        using var _ = shop;
        var templateId = await TemplateAsync(shop);
        Assert.True(Assert.Single(await Templates(shop).CandidatesAsync(templateId)).HasPhoto);
        await Templates(shop).LinkAsync(templateId, [id], "tophattwaffle");
        Assert.Equal("Moves the photo into its place", Assert.Single(await Templates(shop).UsesAsync(templateId)).Change);

        await ApplyAndPushAsync(shop, templateId);
        Assert.All(shop.Writes, w => Assert.Equal("POST", w.Method));
        Assert.All(shop.Writes, w => Assert.DoesNotContain("image=<file", w.Body)); // no upload: the same photo, new ranks
        Assert.Equal(["Front", "Side", SwatchAlt], EtsyOrder(shop));
    }

    [Fact]
    public async Task A_listing_already_in_order_is_left_alone_whatever_Etsys_raw_ranks()
    {
        // Ranks with a gap (as on a real listing): the order is right, so nothing changes and nothing is renumbered.
        var (shop, id) = await SetupAsync((11, 1, "Front"), (12, 2, "Side"), (13, 4, SwatchAlt));
        using var _ = shop;
        var templateId = await TemplateAsync(shop);
        await Templates(shop).LinkAsync(templateId, [id], "tophattwaffle");
        Assert.Equal(PhotoTemplateUseStatus.UpToDate, Assert.Single(await Templates(shop).UsesAsync(templateId)).Status);
        Assert.Equal(0, await Templates(shop).ApplyAsync(templateId, "tophattwaffle"));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public void Templates_fill_from_the_end_in_their_order_after_the_listings_own_photos()
    {
        JsonObject Photo(long id, string alt) => new() { ["etsy_image_id"] = id, ["rank"] = (int)(id - 10), ["alt_text"] = alt };
        var local = new JsonArray(Photo(11, "Specials"), Photo(12, "Front"), Photo(13, SwatchAlt), Photo(14, "Side"));
        var swatch = new PhotoTemplate { Id = 1, FileName = "s.jpg", AltText = SwatchAlt, RankFromEnd = 2 };
        var specials = new PhotoTemplate { Id = 2, FileName = "p.jpg", AltText = "Specials", RankFromEnd = 1 };
        var plan = PhotoTemplateService.Plan(local, [
            (swatch, new ListingPhotoTemplate { AppliedFileName = "s.jpg" }),
            (specials, new ListingPhotoTemplate { AppliedFileName = "p.jpg" })]);
        Assert.Equal(["Front", "Side", SwatchAlt, "Specials"], plan.Images.Select(p => (string)p!["alt_text"]!));
        Assert.Equal([1, 2, 3, 4], plan.Images.Select(p => (int)p!["rank"]!));
        Assert.Empty(plan.Uploads);

        // At 20 photos, adding a template is reported rather than applied.
        var full = new JsonArray([.. Enumerable.Range(1, PhotoEditor.MaxPhotos).Select(i => (JsonNode)Photo(100 + i, $"Photo {i}"))]);
        Assert.True(PhotoTemplateService.Plan(full, [(swatch, new ListingPhotoTemplate())]).TooMany);
    }

    [Fact]
    public async Task Template_rules_and_the_image_cleanup_keeps_template_files()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;
        await TemplateAsync(shop);
        Assert.Equal("Another template has the same alt text; each template needs its own (it identifies the photo on listings).",
            (await Templates(shop).SaveAsync(null, "Other", await FileAsync(shop, 5), SwatchAlt, 1, "tophattwaffle")).Error);
        Assert.Equal("Add the template's image.", (await Templates(shop).SaveAsync(null, "Other", "missing.jpg", "Other alt", 1, "tophattwaffle")).Error);

        var file = (await Templates(shop).GetAsync(1))!.FileName;
        var store = shop.Provider.GetRequiredService<ImageStore>();
        shop.Time.Now = DateTimeOffset.UtcNow.AddDays(3); // the file is older than the cleanup's margin
        await shop.Provider.GetRequiredService<ImageDownloader>().DeleteOrphansAsync(CancellationToken.None);
        Assert.True(store.Exists(file));
    }
}
