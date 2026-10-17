using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ListingCloneTests
{
    private const int OtherShopId = 2;
    private const long OtherEtsyShopId = 43;

    /// <summary>
    /// Listing 1 (Small/Large, photos 11 and 12 downloaded, attribute Primary color) in shop 1 (profiles 501/601/701,
    /// section 801), and a second connected shop with its own profiles (502/602/702, section 802).
    /// </summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var store = shop.Provider.GetRequiredService<ImageStore>();
        await using var db = shop.Db.CreateDbContext();
        foreach (var image in await db.ListingImages.ToListAsync())
            image.FileName = await store.SaveAsync([1, 2, (byte)image.Rank], ".jpg"); // as the photo download job would
        db.Shops.Add(new Shop
        {
            Id = OtherShopId, EtsyShopId = OtherEtsyShopId, EtsyUserId = 222, Name = "OtherShop", CurrencyCode = "USD",
            ConnectedAtUtc = shop.Time.Now.UtcDateTime,
            Token = new ShopToken { EncryptedAccessToken = "x", EncryptedRefreshToken = "x", Scopes = "listings_w" },
        });
        foreach (var (shopId, n) in new[] { (OtherShopId, 2) }) // shop 1's (501, 601, 701, 801) came with the import
        {
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = shopId, EtsyShippingProfileId = 500 + n, Title = $"Shipping {n}" });
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = shopId, ReadinessStateId = 600 + n, ReadinessState = "ready_to_ship", DisplayLabel = $"Processing {n}" });
            db.ReturnPolicies.Add(new ReturnPolicy { ShopId = shopId, EtsyReturnPolicyId = 700 + n, AcceptsReturns = true });
            db.ShopSections.Add(new ShopSection { ShopId = shopId, EtsySectionId = 800 + n, Title = $"Section {n}" });
        }
        await db.SaveChangesAsync();
        var tokens = (InMemoryTokenStore)shop.Provider.GetRequiredService<IEtsyTokenStore>();
        tokens.Tokens[OtherShopId] = tokens.Tokens[FakeEtsyShop.ShopId] with { AccessToken = "222.access" };
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static ListingCloner Cloner(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingCloner>();

    private static Task RunAsync(FakeEtsyShop s, int cloneId) =>
        s.Provider.GetRequiredService<ListingCloneJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = ListingCloneJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId), JobJson.Options),
        }, CancellationToken.None);

    private static JsonObject Draft(FakeEtsyShop s) => s.Listings.Single(l => (string?)l["state"] == "draft");

    [Fact]
    public async Task A_clone_in_the_same_shop_copies_fields_variations_and_photos_into_a_draft()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var choice = await Cloner(shop).DefaultsAsync(id, FakeEtsyShop.ShopId);
        Assert.Equal((501L, 601L, 701L, 801L), (choice.ShippingProfileId, choice.ReadinessStateId, choice.ReturnPolicyId, choice.ShopSectionId));
        choice = choice with { Title = "Galaxy vase (copy)" };

        var plan = await Cloner(shop).PlanAsync(id, choice);
        Assert.True(plan.CanClone, string.Join("; ", plan.Blockers));
        Assert.StartsWith("POST /v3/application/shops/42/listings  (createDraftListing: \"Galaxy vase (copy)\"", plan.Calls[0]);
        Assert.Contains(plan.Warnings, w => w.Contains("created as a draft"));
        Assert.Empty(shop.Writes); // planning sends nothing

        var (cloneId, blockers) = await Cloner(shop).RequestAsync(id, choice, "tophattwaffle");
        Assert.Empty(blockers);
        await RunAsync(shop, cloneId!.Value);

        Assert.Equal(["POST", "PUT", "PUT", "POST", "POST"], shop.Writes.Select(w => w.Method)); // draft, attribute, inventory, 2 photos
        var create = System.Web.HttpUtility.ParseQueryString(shop.Writes[0].Body);
        Assert.Equal(("Galaxy vase (copy)", "19.99", "5", "501", "601", "701", "801", "vase,pla", "physical"),
            (create["title"], create["price"], create["quantity"], create["shipping_profile_id"], create["readiness_state_id"],
             create["return_policy_id"], create["shop_section_id"], create["tags"], create["type"]));
        Assert.Equal("/v3/application/shops/42/listings/" + (long)Draft(shop)["listing_id"]! + "/properties/200", shop.Writes[1].Path);
        var inventory = JsonNode.Parse(shop.Writes[2].Body)!;
        Assert.Equal(2, inventory["products"]!.AsArray().Count);
        Assert.Equal([19.99m, 29.99m], inventory["products"]!.AsArray().Select(p => (decimal)p!["offerings"]![0]!["price"]!));
        Assert.All(shop.Writes.Skip(3), w => Assert.Contains("overwrite=false", w.Body));
        Assert.Equal([1, 2], Draft(shop)["images"]!.AsArray().Select(i => (int)i!["rank"]!).Order());

        await using var db = shop.Db.CreateDbContext();
        var clone = await db.ListingClones.SingleAsync();
        Assert.Equal(ListingCloneStatus.Succeeded, clone.Status);
        Assert.Equal((long)Draft(shop)["listing_id"]!, clone.NewEtsyListingId);
        var copy = await db.Listings.SingleAsync(l => l.Id == clone.NewListingId);
        Assert.Equal(("draft", "Galaxy vase (copy)"), (copy.State, copy.Title));
        Assert.DoesNotContain(shop.Writes, w => w.Body.Contains("state=active")); // never published
    }

    [Fact]
    public async Task A_clone_to_the_other_shop_uses_that_shops_chosen_profiles_everywhere()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var choice = await Cloner(shop).DefaultsAsync(id, OtherShopId);
        Assert.Null(choice.ShippingProfileId); // nothing chosen for that shop yet
        Assert.Contains((await Cloner(shop).PlanAsync(id, choice)).Blockers, b => b.Contains("Choose a shipping profile of OtherShop"));

        choice = choice with { ShippingProfileId = 502, ReadinessStateId = 602, ReturnPolicyId = 702, ShopSectionId = 802 };
        var (cloneId, _) = await Cloner(shop).RequestAsync(id, choice, "tophattwaffle");
        await RunAsync(shop, cloneId!.Value);

        Assert.StartsWith($"/v3/application/shops/{OtherEtsyShopId}/listings", shop.Writes[0].Path);
        var create = System.Web.HttpUtility.ParseQueryString(shop.Writes[0].Body);
        Assert.Equal(("502", "602", "702", "802"), (create["shipping_profile_id"], create["readiness_state_id"], create["return_policy_id"], create["shop_section_id"]));
        var inventory = JsonNode.Parse(shop.Writes.Single(w => w.Path.EndsWith("/inventory")).Body)!;
        Assert.All(inventory["products"]!.AsArray(), p => Assert.Equal(602, (long)p!["offerings"]![0]!["readiness_state_id"]!));
        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(OtherShopId, (await db.Listings.SingleAsync(l => l.Id == db.ListingClones.Single().NewListingId)).ShopId);

        // The next clone into that shop starts from these choices.
        var next = await Cloner(shop).DefaultsAsync(id, OtherShopId);
        Assert.Equal((502L, 602L, 702L, 802L), (next.ShippingProfileId, next.ReadinessStateId, next.ReturnPolicyId, next.ShopSectionId));
    }

    [Fact]
    public async Task A_rejection_after_the_draft_exists_stops_and_says_what_is_on_Etsy()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var choice = (await Cloner(shop).DefaultsAsync(id, FakeEtsyShop.ShopId)) with { Title = "Copy" };
        var (cloneId, _) = await Cloner(shop).RequestAsync(id, choice, "tophattwaffle");
        shop.FailImageMovesWith = "Photo rejected"; // photo uploads fail

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunAsync(shop, cloneId!.Value));

        Assert.Single(shop.Writes, w => w.Path.EndsWith("/images")); // stopped at the first photo
        await using var db = shop.Db.CreateDbContext();
        var clone = await db.ListingClones.SingleAsync();
        Assert.Equal(ListingCloneStatus.Failed, clone.Status);
        Assert.NotNull(clone.NewEtsyListingId);
        Assert.Contains("exists on Etsy but is incomplete", clone.Error);
        Assert.Contains("Created draft", clone.Log);
        Assert.Null(clone.NewListingId);
    }

    [Fact]
    public async Task Photos_not_downloaded_yet_and_a_reused_clone_are_refused()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        await using (var db = shop.Db.CreateDbContext())
            await db.ListingImages.Take(1).ExecuteUpdateAsync(s => s.SetProperty(i => i.FileName, (string?)null));
        var choice = await Cloner(shop).DefaultsAsync(id, FakeEtsyShop.ShopId);
        Assert.Contains((await Cloner(shop).PlanAsync(id, choice)).Blockers, b => b.StartsWith("Photo 1 isn't downloaded"));
        Assert.Null((await Cloner(shop).RequestAsync(id, choice, "tophattwaffle")).CloneId);
        Assert.Empty(shop.Writes);
    }
}
