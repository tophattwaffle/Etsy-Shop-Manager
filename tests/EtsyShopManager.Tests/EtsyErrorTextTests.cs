using System.Net;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class EtsyErrorTextTests
{
    // Etsy's real replies, from the write log and service log (2026-10-17).
    private const string AllCaps = """[{"path":"\/title\/0","type":"all_caps","message":"more than 3 start with 2 sequential capital letters","transformed":false}]""";
    private const string PhotoOrder = """{"error":"The ListingImages for this Listing are in an inconsistent order."}""";

    [Fact]
    public void Etsys_field_checks_name_the_field_and_say_what_to_do()
    {
        Assert.Equal("Etsy rejected the new draft listing: the title: more than 3 words start with two capital letters. Change the title and try again.",
            EtsyErrorText.Describe(HttpStatusCode.BadRequest, AllCaps, HttpMethod.Post, "/v3/application/shops/27763752/listings"));

        // An unknown check keeps Etsy's own words; several fields are listed together.
        Assert.Equal("Etsy rejected the listing changes: the tags: tag is too long; the shipping profile: not found. Change the tags and shipping profile and try again.",
            EtsyErrorText.Describe(HttpStatusCode.BadRequest,
                """[{"path":"/tags/2","type":"length","message":"tag is too long."},{"path":"/shipping_profile_id","message":"not   found"}]""",
                HttpMethod.Patch, "/v3/application/shops/1/listings/2"));
    }

    [Fact]
    public void Known_errors_are_reworded_and_others_cleaned_up()
    {
        Assert.Equal("Etsy rejected the photo change: Etsy has two of this listing's photos at the same position. Push the photos again: " +
                     "the app gives every photo its own position.",
            EtsyErrorText.Describe(HttpStatusCode.BadRequest, PhotoOrder, HttpMethod.Post, "/v3/application/shops/1/listings/2/images"));
        Assert.Equal("Etsy rejected the variations, prices and stock: Offering price must be at least 0.20.",
            EtsyErrorText.Describe(HttpStatusCode.BadRequest, """{"error":"Offering price must be at least 0.20."}""", HttpMethod.Put, "/v3/application/listings/2/inventory"));
    }

    [Fact]
    public void Status_codes_without_a_reason_get_plain_words()
    {
        Assert.Equal("Etsy rejected a category attribute: the app isn't allowed to do this for the shop. Reconnect the shop on the Shops page, then try again.",
            EtsyErrorText.Describe(HttpStatusCode.Forbidden, "", HttpMethod.Put, "/v3/application/shops/1/listings/2/properties/200"));
        Assert.Equal("Etsy had a problem on its side with the personalization questions (error 502). Try again in a few minutes.",
            EtsyErrorText.Describe(HttpStatusCode.BadGateway, "<html>Bad gateway</html>", HttpMethod.Post, "/v3/application/shops/1/listings/2/personalization"));
        Assert.Equal("Etsy couldn't provide the listing: Etsy couldn't find it (it may have been deleted on Etsy).",
            EtsyErrorText.Describe(HttpStatusCode.NotFound, "", HttpMethod.Get, "/v3/application/listings/2"));
        Assert.Equal("Etsy rejected the change: no reason given (error 400).",
            EtsyErrorText.Describe(HttpStatusCode.BadRequest, "<html>oops</html>", HttpMethod.Post, "/v3/application/somewhere"));
    }

    [Fact]
    public async Task A_rejected_push_shows_Etsys_reason_in_plain_words_and_keeps_the_raw_reply()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var id = (await db.Listings.SingleAsync()).Id;
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(id, new Dictionary<string, JsonNode?> { ["description"] = "New text" }, "tophattwaffle");
        var (pushId, _) = await editor.RequestPushAsync(id, "tophattwaffle");
        shop.FailWritesWith = HttpStatusCode.BadRequest;
        shop.FailWritesBody = """[{"path":"/description","type":"length","message":"is too short"}]""";

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId!.Value), JobJson.Options),
        }, CancellationToken.None));

        const string expected = "Etsy rejected the listing changes: the description: is too short. Change the description and try again.";
        Assert.Equal(expected, ex.Message); // what the Jobs page and the toast show
        Assert.Equal(expected, (await db.ListingPushes.SingleAsync()).Error); // what the editor's push history shows
        Assert.Equal(shop.FailWritesBody, Assert.IsType<EtsyApiException>(ex).ResponseBody); // raw reply kept
    }

    [Fact]
    public void Title_rules_catch_what_Etsy_rejects_and_pass_real_titles()
    {
        // The title Etsy rejected for clone job 497, and the one it then accepted.
        Assert.Equal(["The title has 4 words starting with two capital letters (TEST, LISTING!!!, APP, CLONE); Etsy allows 3. Write some of them in normal case."],
            ListingTextRules.TitleProblems("TEST LISTING!!! APP CLONE"));
        Assert.Empty(ListingTextRules.TitleProblems("TEST LISTING!!! app clone"));
        Assert.Empty(ListingTextRules.TitleProblems("GameCube Controller Trigger Plugs Set | Smash Brothers Melee | SSBM | Smash Bros | Ultimate | SSBU | Smash Bros"));
        Assert.Empty(ListingTextRules.TitleProblems("Counter-Strike Map Layout Wall Art | CS2 | CSGO | 3D Print™ | Dust2 & Mirage: 100% fun + more"));

        Assert.Equal(["The title uses \"&\" 2 times; Etsy allows it once."], ListingTextRules.TitleProblems("Salt & Pepper & Spice"));
        Assert.Equal(["The title can't contain \"\U0001F643\". Etsy allows letters, numbers, punctuation, spaces and ™ © ®."],
            ListingTextRules.TitleProblems("Desk toy \U0001F643"));
        Assert.Equal(["The tag \"gift!\" can't contain \"!\". Etsy allows letters, numbers, spaces, - and ' in tags."],
            ListingTextRules.TagProblems(["gift!", "mom's day", "t-rex"]));
        Assert.Equal(["The material \"PLA+\" can't contain \"+\". Etsy allows only letters, numbers and spaces in materials."],
            ListingTextRules.MaterialProblems(["PLA+", "PETG"]));
    }

    [Fact]
    public async Task The_review_blocks_a_title_Etsy_would_reject_before_anything_is_sent()
    {
        var shop = new FakeEtsyShop();
        using var _ = shop;
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var id = (await db.Listings.SingleAsync()).Id;

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = (await cloner.DefaultsAsync(id, FakeEtsyShop.ShopId)) with { Title = "TEST LISTING!!! APP CLONE" };
        Assert.Contains((await cloner.PlanAsync(id, choice)).Blockers, b => b.StartsWith("The title has 4 words starting with two capital letters"));
        Assert.Null((await cloner.RequestAsync(id, choice, "tophattwaffle")).CloneId);

        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "GALAXY VASE FOR YOUR DESK", ["tags"] = new JsonArray("vase!") }, "tophattwaffle");
        var preview = await editor.PreviewAsync(id);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.StartsWith("The title has 5 words starting with two capital letters"));
        Assert.Contains(preview.Blockers, b => b.StartsWith("The tag \"vase!\" can't contain"));
        Assert.Empty(shop.Writes);
    }
}
