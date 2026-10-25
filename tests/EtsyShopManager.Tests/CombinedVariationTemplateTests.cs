using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

/// <summary>A listing variation following several variation templates at once (e.g. "Basic Colors" then "Special Colors").</summary>
public class CombinedVariationTemplateTests
{
    private const long Color = VariationEditor.CustomProperty1;

    /// <summary>
    /// One listing with a single Color variation, the price and stock shared by every color (as TopHATTwaffle's listings are):
    /// each color $20.00, stock 5.
    /// </summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync(params string[] colors)
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        long id = 10;
        listing["inventory"] = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. colors.Select((c, i) =>
            {
                var p = FakeEtsyShop.Product(++id, "", c, 2000, 5);
                var pv = p["property_values"]![0]!;
                (pv["property_id"], pv["property_name"], pv["value_ids"]) = (Color, "Color", new JsonArray(100L + i));
                return (JsonNode)p;
            })]),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, await db.Listings.Where(l => l.EtsyListingId == 1).Select(l => l.Id).SingleAsync());
    }

    private static VariationListService Lists(FakeEtsyShop s) => s.Provider.GetRequiredService<VariationListService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();
    private static async Task<JsonNode> InventoryAsync(FakeEtsyShop s, int id) => (await Editor(s).LoadAsync(id))!.Model.Local(ListingFields.Inventory)!;
    private static IReadOnlyList<string> Colors(JsonNode inventory) => VariationEditor.Variations(inventory).Single(v => v.PropertyId == Color).Values;

    private static async Task<int> TemplateAsync(FakeEtsyShop s, string name, params string[] options) =>
        (await Lists(s).SaveAsync(null, name, [.. options.Select(o => new VariationListOption(0, o, true))], "tophattwaffle")).Id!.Value;

    private static async Task PushAsync(FakeEtsyShop shop, int pushId) =>
        await shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    [Fact]
    public async Task Two_templates_drive_one_variation_in_order_and_an_edit_to_either_flows_to_the_listing()
    {
        var (shop, listing) = await SetupAsync("Black", "White", "Red", "Gold", "Silver");
        using var _ = shop;
        var basic = await TemplateAsync(shop, "Basic Colors", "Black", "White", "Red", "Blue");
        var special = await TemplateAsync(shop, "Special Colors", "Gold", "Silver", "Copper");

        // Basic matches 3 of 5 values; once linked, Special is offered on the values Basic doesn't cover (2 of 2).
        Assert.Equal(1, await Lists(shop).LinkAsync(basic, [(listing, Color)], "tophattwaffle"));
        var candidate = Assert.Single(await Lists(shop).CandidatesAsync(special));
        Assert.Equal((2, 2), (candidate.InList, candidate.Values));
        Assert.Equal(["Basic Colors"], candidate.CombinedWith!);
        Assert.Equal(1, await Lists(shop).LinkAsync(special, [(listing, Color)], "tophattwaffle"));
        Assert.Empty(await Lists(shop).CandidatesAsync(special));
        Assert.Equal(["Basic Colors", "Special Colors"], (await Lists(shop).ListingLinksAsync(listing)).Select(l => l.ListName));

        var use = Assert.Single(await Lists(shop).UsesAsync(special));
        Assert.Equal(["Basic Colors"], use.CombinedWith!);
        Assert.Equal(VariationListUseStatus.HasChanges, use.Status);
        Assert.Equal(["Color:", "Adds Blue", "Adds Copper"], use.Changes);

        // Applying from either template brings the whole variation in line: one order, Basic's options then Special's.
        Assert.Equal(1, await Lists(shop).ApplyAsync(special, "tophattwaffle"));
        Assert.Equal(["Black", "White", "Red", "Blue", "Gold", "Silver", "Copper"], Colors(await InventoryAsync(shop, listing)));
        Assert.Equal(VariationListUseStatus.UpToDate, Assert.Single(await Lists(shop).UsesAsync(basic)).Status);
        Assert.Equal(VariationListUseStatus.UpToDate, Assert.Single(await Lists(shop).UsesAsync(special)).Status);

        // A color added to Basic reaches the listing that combines it, in Basic's part of the order.
        var saved = (await Lists(shop).GetAsync(basic))!;
        await Lists(shop).SaveAsync(basic, saved.Name, [.. saved.Options, new VariationListOption(0, "Green", true)], "tophattwaffle");
        Assert.Equal(["Color:", "Adds Green"], Assert.Single(await Lists(shop).UsesAsync(basic)).Changes);
        Assert.Equal(1, await Lists(shop).ApplyAsync(basic, "tophattwaffle"));
        Assert.Equal(["Black", "White", "Red", "Blue", "Green", "Gold", "Silver", "Copper"], Colors(await InventoryAsync(shop, listing)));
        Assert.Empty(shop.Writes); // applying only writes drafts

        // The push sends the shared price and stock exactly as a single template's push does.
        var review = Assert.Single(await Lists(shop).ReviewAsync(basic));
        Assert.True(review.Preview.CanPush, string.Join("; ", review.Preview.Blockers));
        await PushAsync(shop, Assert.Single(await Lists(shop).PushAsync(basic, "tophattwaffle")).PushId!.Value);
        var body = JsonNode.Parse(shop.Writes.Single(w => w.Path == "/v3/application/listings/1/inventory").Body)!;
        var products = body["products"]!.AsArray().Select(p => p!).ToList();
        Assert.Equal(["Black", "White", "Red", "Blue", "Green", "Gold", "Silver", "Copper"],
            products.Select(p => (string)p["property_values"]![0]!["values"]![0]!));
        Assert.All(products, p => Assert.Equal((20m, 5, true),
            ((decimal)p["offerings"]![0]!["price"]!, (int)p["offerings"]![0]!["quantity"]!, (bool)p["offerings"]![0]!["is_enabled"]!)));
        Assert.Empty(body["price_on_property"]!.AsArray());
        Assert.Empty(body["quantity_on_property"]!.AsArray());
    }

    [Fact]
    public async Task An_option_in_both_templates_is_kept_once_and_follows_the_first_template_only()
    {
        var (shop, listing) = await SetupAsync("Black", "White");
        using var _ = shop;
        var basic = await TemplateAsync(shop, "Basic Colors", "Black", "White");
        var special = await TemplateAsync(shop, "Special Colors", "White", "Gold");
        Assert.Equal(1, await Lists(shop).LinkAsync(basic, [(listing, Color)], "tophattwaffle"));

        var (preview, error) = await Lists(shop).PreviewLinkAsync(special, listing, Color);
        Assert.Null(error);
        Assert.Contains("White is in both Basic Colors and Special Colors; the listing has it once, following Basic Colors.", preview!.Notes);
        Assert.Null(await Lists(shop).LinkVariationAsync(special, listing, Color, "tophattwaffle"));
        await Lists(shop).ApplyAsync(special, "tophattwaffle");
        Assert.Equal(["Black", "White", "Gold"], Colors(await InventoryAsync(shop, listing)));

        // Special switching White off, or dropping it, doesn't touch the White that Basic gives the listing.
        var saved = (await Lists(shop).GetAsync(special))!;
        await Lists(shop).SaveAsync(special, saved.Name, [.. saved.Options.Select(o => o.Name == "White" ? o with { Enabled = false } : o)], "tophattwaffle");
        var use = Assert.Single(await Lists(shop).UsesAsync(special));
        Assert.Equal(VariationListUseStatus.UpToDate, use.Status);
        await Lists(shop).SaveAsync(special, saved.Name, [.. saved.Options.Where(o => o.Name != "White")], "tophattwaffle");
        Assert.Equal(0, await Lists(shop).ApplyAsync(special, "tophattwaffle"));
        var inventory = await InventoryAsync(shop, listing);
        Assert.Equal(["Black", "White", "Gold"], Colors(inventory));
        Assert.True(VariationEditor.IsValueAvailable(inventory, Color, "White"));

        // Basic switching it off does.
        saved = (await Lists(shop).GetAsync(basic))!;
        await Lists(shop).SaveAsync(basic, saved.Name, [.. saved.Options.Select(o => o.Name == "White" ? o with { Enabled = false } : o)], "tophattwaffle");
        Assert.Equal(1, await Lists(shop).ApplyAsync(basic, "tophattwaffle"));
        Assert.False(VariationEditor.IsValueAvailable(await InventoryAsync(shop, listing), Color, "White"));
    }

    [Fact]
    public async Task Unlinking_one_template_keeps_its_values_and_the_other_template_keeps_working()
    {
        var (shop, listing) = await SetupAsync("Black", "White", "Gold", "Silver");
        using var _ = shop;
        var basic = await TemplateAsync(shop, "Basic Colors", "Black", "White");
        var special = await TemplateAsync(shop, "Special Colors", "Gold", "Silver");
        await Lists(shop).LinkAsync(basic, [(listing, Color)], "tophattwaffle");
        await Lists(shop).LinkAsync(special, [(listing, Color)], "tophattwaffle");

        await Lists(shop).UnlinkAsync(basic, listing, "tophattwaffle");
        Assert.Empty(await Lists(shop).UsesAsync(basic));
        var use = Assert.Single(await Lists(shop).UsesAsync(special));
        Assert.Empty(use.CombinedWith!);
        Assert.Equal(["Black", "White", "Gold", "Silver"], Colors(await InventoryAsync(shop, listing))); // nothing removed

        var saved = (await Lists(shop).GetAsync(special))!;
        await Lists(shop).SaveAsync(special, saved.Name, [.. saved.Options, new VariationListOption(0, "Copper", true)], "tophattwaffle");
        await Lists(shop).ApplyAsync(special, "tophattwaffle");
        var colors = Colors(await InventoryAsync(shop, listing));
        Assert.Equal(["Gold", "Silver", "Copper", "Black", "White"], colors); // Basic's colors stay, now the listing's own
    }

    [Fact]
    public async Task Moving_a_template_earlier_reorders_the_values_on_the_next_apply()
    {
        var (shop, listing) = await SetupAsync("Black", "White", "Gold", "Silver");
        using var _ = shop;
        var basic = await TemplateAsync(shop, "Basic Colors", "Black", "White");
        var special = await TemplateAsync(shop, "Special Colors", "Gold", "Silver");
        await Lists(shop).LinkAsync(basic, [(listing, Color)], "tophattwaffle");
        Assert.Null(await Lists(shop).LinkVariationAsync(special, listing, Color, "tophattwaffle"));
        Assert.Equal("Color already follows Special Colors.", await Lists(shop).LinkVariationAsync(special, listing, Color, "tophattwaffle"));

        await Lists(shop).MoveAsync(special, listing, -1, "tophattwaffle");
        Assert.Equal(["Special Colors", "Basic Colors"], (await Lists(shop).ListingLinksAsync(listing)).Select(l => l.ListName));
        Assert.Equal(["Black", "White", "Gold", "Silver"], Colors(await InventoryAsync(shop, listing))); // only recorded so far
        Assert.Equal(["Color:", "Reorders options in Color"], Assert.Single(await Lists(shop).UsesAsync(basic)).Changes);
        Assert.Equal(1, await Lists(shop).ApplyAsync(basic, "tophattwaffle", listingId: listing));
        Assert.Equal(["Gold", "Silver", "Black", "White"], Colors(await InventoryAsync(shop, listing)));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Templates_that_together_go_over_Etsys_option_limit_are_refused_before_anything_is_sent()
    {
        var basicOptions = Enumerable.Range(1, 40).Select(i => $"Basic {i}").ToArray();
        var specialOptions = Enumerable.Range(1, 35).Select(i => $"Special {i}").ToArray();
        var (shop, listing) = await SetupAsync([.. basicOptions]);
        using var _ = shop;
        var basic = await TemplateAsync(shop, "Basic Colors", basicOptions);
        var special = await TemplateAsync(shop, "Special Colors", specialOptions);
        Assert.Equal(1, await Lists(shop).LinkAsync(basic, [(listing, Color)], "tophattwaffle"));

        const string limit = "Applying Basic Colors and Special Colors would give this variation 75 options; Etsy allows at most 70.";
        var (preview, previewError) = await Lists(shop).PreviewLinkAsync(special, listing, Color);
        Assert.Null(preview);
        Assert.StartsWith(limit, previewError);
        Assert.StartsWith(limit, await Lists(shop).LinkVariationAsync(special, listing, Color, "tophattwaffle"));
        Assert.Equal(0, await Lists(shop).LinkAsync(special, [(listing, Color)], "tophattwaffle"));
        Assert.Single(await Lists(shop).ListingLinksAsync(listing));

        // Switching options off later can't sneak past it either: a template growing past the limit isn't applied.
        var saved = (await Lists(shop).GetAsync(basic))!;
        await Lists(shop).SaveAsync(basic, saved.Name, [.. saved.Options, .. Enumerable.Range(41, 31).Select(i => new VariationListOption(0, $"Basic {i}", true))], "tophattwaffle");
        var use = Assert.Single(await Lists(shop).UsesAsync(basic));
        Assert.Equal(VariationListUseStatus.TooManyOptions, use.Status);
        Assert.StartsWith("Applying Basic Colors would give this variation 71 options; Etsy allows at most 70.", use.Notes[0]);
        Assert.Equal(0, await Lists(shop).ApplyAsync(basic, "tophattwaffle"));
        Assert.Equal(40, Colors(await InventoryAsync(shop, listing)).Count);

        // A variation edited past the limit by hand is a push blocker with the reason.
        var inventory = await InventoryAsync(shop, listing);
        foreach (var i in Enumerable.Range(1, 31)) inventory = VariationEditor.AddValue(inventory, Color, $"Own {i}");
        await Editor(shop).SaveAsync(listing, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = inventory }, "tophattwaffle");
        var push = await Editor(shop).PreviewAsync(listing);
        Assert.Contains("Color has 71 options; Etsy allows at most 70 in a variation. Remove some, or switch them off in its variation templates and apply again.",
            push.Blockers);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public void A_single_template_plans_exactly_as_before()
    {
        var inventory = new JsonObject
        {
            ["price_on_property"] = new JsonArray(),
            ["products"] = new JsonArray([.. new[] { "Red", "Black", "Mine" }.Select((c, i) =>
            {
                var p = FakeEtsyShop.Product(i + 1, "", c, 2000, 5);
                var pv = p["property_values"]![0]!;
                (pv["property_id"], pv["property_name"]) = (Color, "Color");
                return (JsonNode)p;
            })]),
        };
        var list = new VariationList { Id = 1, Name = "Basic Colors", Options = [new(1, "Black", true), new(2, "Red", true), new(3, "Blue", true)] };
        var link = new ListingVariationList { PropertyId = Color, AppliedOptions = [new(1, "Black", true), new(2, "Red", true)] };

        var single = VariationListService.Plan(list, link, inventory);
        var combined = VariationListService.PlanCombined([new VariationListSource(list, link)], inventory);
        Assert.True(JsonNode.DeepEquals(single.Inventory, combined.Inventory));
        Assert.Equal(single.Changes, combined.Changes);
        Assert.Equal(single.Applied, Assert.Single(combined.Applied));
        Assert.Null(combined.Blocker);
    }
}
