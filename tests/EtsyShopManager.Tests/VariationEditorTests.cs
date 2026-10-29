using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class VariationEditorTests
{
    private static JsonObject Pv(long id, string? name, string value, params long[] ids) => new()
    {
        ["property_id"] = id, ["property_name"] = name, ["scale_id"] = null,
        ["value_ids"] = new JsonArray([.. ids.Select(i => (JsonNode)i)]), ["values"] = new JsonArray(value),
    };

    private static JsonObject Product(long price, bool enabled = true, params JsonObject[] pvs) => new()
    {
        ["sku"] = "",
        ["property_values"] = new JsonArray(pvs),
        ["offerings"] = new JsonArray(new JsonObject
        {
            ["price_amount"] = price, ["price_divisor"] = 100, ["is_enabled"] = enabled, ["readiness_state_id"] = null,
        }),
    };

    /// <summary>Size (100) × Primary color (200), price by size: Small 19.99, Large 29.99.</summary>
    private static JsonObject SizeByColor() => VariationEditor.Canonical(new JsonObject
    {
        ["price_on_property"] = new JsonArray(100L),
        ["quantity_on_property"] = new JsonArray(),
        ["sku_on_property"] = new JsonArray(),
        ["readiness_state_on_property"] = new JsonArray(),
        ["products"] = new JsonArray(
            Product(1999, true, Pv(100, "Size", "Small", 1), Pv(200, "Primary color", "Black", 11)),
            Product(1999, true, Pv(100, "Size", "Small", 1), Pv(200, "Primary color", "White", 12)),
            Product(2999, true, Pv(100, "Size", "Large", 2), Pv(200, "Primary color", "Black", 11)),
            Product(2999, true, Pv(100, "Size", "Large", 2), Pv(200, "Primary color", "White", 12))),
    });

    private static string Name(JsonNode p) => EditorLookups.VariationName(p);
    private static long Price(JsonNode p) => (long)p["offerings"]![0]!["price_amount"]!;
    private static Dictionary<string, long> Prices(JsonNode inv) => inv["products"]!.AsArray().ToDictionary(p => Name(p!), p => Price(p!));

    [Fact]
    public void Variations_list_each_one_with_its_values()
    {
        var v = VariationEditor.Variations(SizeByColor());

        Assert.Equal([(100L, "Size"), (200L, "Primary color")], v.Select(x => (x.PropertyId, x.Name!)));
        Assert.Equal(["Small", "Large"], v[0].Values); // managed-state order (by Etsy value id, then text)
        Assert.Equal(["Black", "White"], v[1].Values);
    }

    [Fact]
    public void A_new_color_gets_a_combination_per_size_priced_like_that_size()
    {
        var inv = VariationEditor.AddValue(SizeByColor(), 200, " Red ");

        var prices = Prices(inv);
        Assert.Equal(6, prices.Count);
        Assert.Equal(1999, prices["Small / Red"]);
        Assert.Equal(2999, prices["Large / Red"]);
        var red = inv["products"]!.AsArray().First(p => Name(p!) == "Small / Red")!;
        Assert.Empty(red["property_values"]![1]!["value_ids"]!.AsArray()); // Etsy gives it an id on push
        Assert.Equal("Primary color", (string?)red["property_values"]![1]!["property_name"]);
    }

    [Fact]
    public void A_new_size_gets_one_price_for_all_its_colors()
    {
        var inv = VariationEditor.AddValue(SizeByColor(), 100, "Medium");

        var prices = Prices(inv);
        Assert.Equal(prices["Medium / Black"], prices["Medium / White"]); // the price varies by size only
        Assert.Empty(ListingEditorProblems(inv));
    }

    [Fact]
    public void Values_are_checked_when_added_renamed_or_removed()
    {
        Assert.Throws<ArgumentException>(() => VariationEditor.AddValue(SizeByColor(), 200, "black")); // already there
        Assert.Throws<ArgumentException>(() => VariationEditor.AddValue(SizeByColor(), 200, "  "));
        Assert.Throws<ArgumentException>(() => VariationEditor.RenameValue(SizeByColor(), 200, "Black", "White"));
        var oneColor = VariationEditor.RemoveValue(SizeByColor(), 200, "White");
        Assert.Throws<ArgumentException>(() => VariationEditor.RemoveValue(oneColor, 200, "Black")); // the last one
    }

    [Fact]
    public void Removing_a_value_removes_its_combinations()
    {
        var inv = VariationEditor.RemoveValue(SizeByColor(), 200, "White");

        Assert.Equal(["Small / Black", "Large / Black"], inv["products"]!.AsArray().Select(p => Name(p!)));
    }

    [Fact]
    public void Renaming_a_value_keeps_its_prices_and_drops_its_Etsy_id()
    {
        var inv = VariationEditor.RenameValue(SizeByColor(), 100, "Small", "Petite");

        var prices = Prices(inv);
        Assert.Equal(1999, prices["Petite / Black"]);
        var petite = inv["products"]!.AsArray().First(p => Name(p!) == "Petite / Black")!;
        Assert.Empty(petite["property_values"]![0]!["value_ids"]!.AsArray());
    }

    [Fact]
    public void A_new_variation_multiplies_the_combinations()
    {
        var inv = VariationEditor.RemoveVariation(SizeByColor(), 200); // Size only
        inv = VariationEditor.AddVariation(inv, "Finish", ["Matte", "Gloss"]);

        Assert.Equal(["Size", "Finish"], VariationEditor.Variations(inv).Select(v => v.Name));
        Assert.Equal(VariationEditor.CustomProperty1, VariationEditor.Variations(inv)[1].PropertyId);
        var prices = Prices(inv);
        Assert.Equal(4, prices.Count);
        Assert.Equal(1999, prices["Small / Matte"]);
        Assert.Equal(2999, prices["Large / Gloss"]);
        Assert.Throws<ArgumentException>(() => VariationEditor.AddVariation(inv, "Third", ["x"])); // Etsy allows two
        Assert.Throws<ArgumentException>(() => VariationEditor.AddVariation(SizeByColor(), "size", ["x"]));
    }

    [Fact]
    public void Removing_the_variation_the_price_varies_by_leaves_one_price()
    {
        var inv = VariationEditor.RemoveVariation(SizeByColor(), 100);

        Assert.Empty(inv["price_on_property"]!.AsArray());
        Assert.Equal(["Black", "White"], inv["products"]!.AsArray().Select(p => Name(p!)));
        Assert.Single(Prices(inv).Values.Distinct()); // each color kept the first size's price, the same one
        Assert.Empty(ListingEditorProblems(inv));
    }

    [Fact]
    public void A_price_that_stops_varying_takes_one_value()
    {
        var inv = VariationEditor.SetVaries(SizeByColor(), VariationEditor.PriceOnProperty, 100, false);

        Assert.Empty(inv["price_on_property"]!.AsArray());
        Assert.Single(Prices(inv).Values.Distinct());
        inv = VariationEditor.SetVaries(inv, VariationEditor.PriceOnProperty, 200, true);
        Assert.Equal("[200]", inv["price_on_property"]!.ToJsonString());
    }

    [Fact]
    public void Etsys_own_variations_keep_their_name()
    {
        Assert.Throws<ArgumentException>(() => VariationEditor.RenameVariation(SizeByColor(), 200, "Colour"));
        var custom = VariationEditor.AddVariation(VariationEditor.RemoveVariation(SizeByColor(), 200), "Finish", ["Matte"]);
        var renamed = VariationEditor.RenameVariation(custom, VariationEditor.CustomProperty1, "Coating");
        Assert.Equal("Coating", VariationEditor.Variations(renamed)[1].Name);
    }

    [Fact]
    public void The_preview_describes_the_change_in_words()
    {
        var after = VariationEditor.RemoveValue(VariationEditor.AddValue(SizeByColor(), 200, "Red"), 200, "White");

        Assert.Equal(
            ["Primary color: adds Red.", "Primary color: removes White (buyers can no longer choose it)."],
            VariationEditor.Describe(SizeByColor(), after));
    }

    [Fact]
    public void The_preview_lists_price_changes_per_combination()
    {
        var after = SizeByColor();
        foreach (var p in after["products"]!.AsArray().Where(p => Name(p!).StartsWith("Small")))
            p!["offerings"]![0]!["price_amount"] = 2199L;

        Assert.Equal(["Price of Small / Black: 19.99 → 21.99.", "Price of Small / White: 19.99 → 21.99."],
            VariationEditor.Describe(SizeByColor(), after));
    }

    [Fact]
    public void Etsys_new_value_ids_are_adopted_so_the_draft_matches()
    {
        var local = VariationEditor.AddValue(SizeByColor(), 200, "Red");
        var etsy = (JsonObject)local.DeepClone();
        foreach (var pv in etsy["products"]!.AsArray().SelectMany(p => p!["property_values"]!.AsArray())
                     .Where(v => (string?)v!["values"]![0] == "Red"))
            pv!["value_ids"] = new JsonArray(99L);
        etsy = VariationEditor.Canonical(etsy);

        Assert.True(JsonNode.DeepEquals(etsy, VariationEditor.AdoptValueIds(local, etsy)));
    }

    [Fact]
    public void Broken_structures_are_blocked()
    {
        var three = SizeByColor();
        foreach (var p in three["products"]!.AsArray()) p!["property_values"]!.AsArray().Add(Pv(513, "Finish", "Matte"));
        Assert.Contains(ListingEditorProblems(three), b => b.Contains("Etsy allows 2"));

        var missing = SizeByColor();
        missing["products"]!.AsArray().RemoveAt(0);
        Assert.Contains(ListingEditorProblems(missing), b => b.Contains("exactly once"));

        var dup = VariationEditor.Canonical(new JsonObject
        {
            ["price_on_property"] = new JsonArray(), ["quantity_on_property"] = new JsonArray(),
            ["sku_on_property"] = new JsonArray(), ["readiness_state_on_property"] = new JsonArray(),
            ["products"] = new JsonArray(Product(500, true, Pv(513, "Finish", "Matte")), Product(500, true, Pv(513, "Finish", "matte"))),
        });
        Assert.Contains(ListingEditorProblems(dup), b => b.Contains("more than once"));

        var none = VariationEditor.SetAvailable(VariationEditor.SetAvailable(VariationEditor.RemoveVariation(SizeByColor(), 200), 0, false), 1, false);
        Assert.Contains(ListingEditorProblems(none), b => b.Contains("At least one combination"));
    }

    [Fact]
    public void Single_variation_pricing_sets_and_reads_prices_processing_and_availability_per_option()
    {
        var inv = SizeByColor();
        Assert.Equal(19.99m, VariationEditor.GetValuePrice(inv, 100, "Small"));
        Assert.Equal(29.99m, VariationEditor.GetValuePrice(inv, 100, "Large"));

        inv = VariationEditor.SetValuePrice(inv, 100, "Small", 17.50m);
        Assert.Equal(17.50m, VariationEditor.GetValuePrice(inv, 100, "Small"));
        var prices = Prices(inv);
        Assert.Equal(1750, prices["Small / Black"]);
        Assert.Equal(1750, prices["Small / White"]);
        Assert.Equal(2999, prices["Large / Black"]);

        inv = VariationEditor.SetVaries(inv, VariationEditor.ProcessingOnProperty, 100, true);
        inv = VariationEditor.SetValueProcessing(inv, 100, "Large", 77L);
        Assert.Null(VariationEditor.GetValueProcessing(inv, 100, "Small"));
        Assert.Equal(77L, VariationEditor.GetValueProcessing(inv, 100, "Large"));

        // Retiring a color and then toggling a size off and back on keeps the retired color off.
        inv = VariationEditor.SetValueAvailable(inv, 200, "White", false);
        Assert.False(VariationEditor.IsValueAvailable(inv, 200, "White"));
        Assert.True(VariationEditor.IsValueAvailable(inv, 100, "Small"));

        inv = VariationEditor.SetValueAvailable(inv, 100, "Small", false);
        Assert.False(VariationEditor.IsValueAvailable(inv, 100, "Small"));

        inv = VariationEditor.SetValueAvailable(inv, 100, "Small", true);
        Assert.True(VariationEditor.IsValueAvailable(inv, 100, "Small"));
        Assert.False(VariationEditor.IsValueAvailable(inv, 200, "White"));
    }

    [Fact]
    public void Dual_variation_pricing_can_bulk_set_prices_by_one_variation_and_keep_manual_exceptions()
    {
        var inv = VariationEditor.AddValue(SizeByColor(), 200, "Random");
        inv = VariationEditor.SetVaries(inv, VariationEditor.PriceOnProperty, 200, true);

        // Price all colors of Small at 14.99 and Large at 24.99 in one step per size.
        inv = VariationEditor.SetValuePrice(inv, 100, "Small", 14.99m);
        inv = VariationEditor.SetValuePrice(inv, 100, "Large", 24.99m);

        var prices = Prices(inv);
        Assert.Equal(1499, prices["Small / Black"]);
        Assert.Equal(1499, prices["Small / White"]);
        Assert.Equal(1499, prices["Small / Random"]);
        Assert.Equal([14.99m], VariationEditor.GetDistinctValuePrices(inv, 100, "Small"));

        // Adjust Random manually for Small; the mode price stays 14.99 and both distinct prices are reported.
        var smallRandom = inv["products"]!.AsArray().Single(p => Name(p!) == "Small / Random")!;
        smallRandom["offerings"]![0]!["price_amount"] = 1299L;

        Assert.Equal(14.99m, VariationEditor.GetValuePrice(inv, 100, "Small"));
        Assert.Equal([12.99m, 14.99m], VariationEditor.GetDistinctValuePrices(inv, 100, "Small"));
        Assert.Empty(ListingEditorProblems(inv));
    }
    private static void UnusedAnchor()
    {
    }

    /// <summary>The inventory blockers the preview would show for this inventory.</summary>
    private static IReadOnlyList<string> ListingEditorProblems(JsonObject inventory)
    {
        var etsy = new JsonObject { ["inventory"] = new JsonObject { ["products"] = new JsonArray() }, ["state"] = "active" };
        var model = new ListingEditModel(etsy, null, new JsonObject { ["inventory"] = inventory.DeepClone() });
        var listing = new Listing { Shop = null!, EtsyState = new ListingEtsyState { ManagedVersion = ManagedListingState.CurrentVersion } };
        return ListingEditor.Preview(listing, model, pushInProgress: false).Blockers;
    }
}

public class VariationPushTests
{
    private static async Task<(FakeEtsyShop Shop, int ListingId)> ImportedAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<JsonObject> LocalInventoryAsync(FakeEtsyShop s, int id) =>
        (JsonObject)(await Editor(s).LoadAsync(id))!.Model.Local(ListingFields.Inventory)!.DeepClone();

    private static Task RunPushAsync(FakeEtsyShop s, int pushId) =>
        s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    [Fact]
    public async Task Adding_a_size_sends_the_whole_grid_with_existing_stock_untouched_and_finishes_clean()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        var inventory = VariationEditor.AddValue(await LocalInventoryAsync(shop, id), 100, "Medium");
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = inventory }, "tophattwaffle");

        var preview = await Editor(shop).PreviewAsync(id);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains("Size: adds Medium.", preview.Warnings);
        Assert.Contains(preview.Warnings, w => w.StartsWith("1 new combination starts with a stock of 5"));
        Assert.Empty(shop.Writes);

        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);

        var put = Assert.Single(shop.Writes);
        Assert.Equal("PUT", put.Method);
        var expected = JsonNode.Parse("""
            {"products":[
              {"sku":"SKU1-S","property_values":[{"property_id":100,"value_ids":[],"property_name":"Size","values":["Small"]}],
               "offerings":[{"price":19.99,"quantity":5,"is_enabled":true}]},
              {"sku":"SKU1-L","property_values":[{"property_id":100,"value_ids":[],"property_name":"Size","values":["Large"]}],
               "offerings":[{"price":29.99,"quantity":5,"is_enabled":true}]},
              {"sku":"SKU1-L","property_values":[{"property_id":100,"value_ids":[],"property_name":"Size","values":["Medium"]}],
               "offerings":[{"price":29.99,"quantity":5,"is_enabled":true}]}],
             "price_on_property":[100],"quantity_on_property":[],"sku_on_property":[100],"readiness_state_on_property":[]}
            """);
        Assert.True(JsonNode.DeepEquals(expected, JsonNode.Parse(put.Body)), put.Body); // Etsy's order kept, Medium after it

        await using var db = shop.Db.CreateDbContext();
        var listing = await db.Listings.Include(l => l.Products).SingleAsync();
        Assert.False(listing.HasDraft); // Etsy's new value ids were adopted; nothing left to push
        Assert.False(listing.HasDrift);
        Assert.Equal(3, listing.Products.Count(p => !p.IsDeleted));
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
        Assert.Empty(shop.Events.Events.OfType<DriftDetected>());
    }

    [Fact]
    public async Task The_variations_keep_Etsys_order_which_is_the_order_buyers_pick_them_in()
    {
        // Found on 4 real listings: Etsy lists "Style" (513) before "Primary color" (200); managed state sorts by id.
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        foreach (var p in listing["inventory"]!["products"]!.AsArray())
            p!["property_values"]!.AsArray().Insert(0, new JsonObject
            {
                ["property_id"] = 513, ["property_name"] = "Style", ["value_ids"] = new JsonArray(77), ["values"] = new JsonArray("Dust 2"),
            });
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        using var _ = shop;
        int id;
        await using (var db = shop.Db.CreateDbContext()) id = (await db.Listings.SingleAsync()).Id;
        var inventory = VariationEditor.AddValue(await LocalInventoryAsync(shop, id), 100, "Medium");
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = inventory }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");

        await RunPushAsync(shop, pushId!.Value);

        var body = JsonNode.Parse(Assert.Single(shop.Writes).Body)!;
        Assert.All(body["products"]!.AsArray(), p => Assert.Equal([513L, 100L],
            p!["property_values"]!.AsArray().Select(v => (long)v!["property_id"]!)));
    }

    [Fact]
    public async Task Adding_a_variation_and_removing_it_again_round_trips()
    {
        var (shop, id) = await ImportedAsync();
        using var _ = shop;
        var inventory = VariationEditor.AddVariation(await LocalInventoryAsync(shop, id), "Finish", ["Matte", "Gloss"]);
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = inventory }, "tophattwaffle");
        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);

        var body = JsonNode.Parse(shop.Writes[^1].Body)!;
        Assert.Equal(4, body["products"]!.AsArray().Count);
        Assert.All(body["products"]!.AsArray(), p => Assert.Contains(p!["property_values"]!.AsArray(),
            v => (long)v!["property_id"]! == VariationEditor.CustomProperty1 && (string?)v["property_name"] == "Finish"));

        inventory = VariationEditor.RemoveVariation(await LocalInventoryAsync(shop, id), VariationEditor.CustomProperty1);
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Inventory] = inventory }, "tophattwaffle");
        shop.Provider.GetRequiredService<RecordingJobQueue>().Requests.Clear(); // the first push's job is done
        pushId = (await Editor(shop).RequestPushAsync(id, "tophattwaffle")).PushId;
        await RunPushAsync(shop, pushId!.Value);

        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(2, JsonNode.Parse(shop.Writes[^1].Body)!["products"]!.AsArray().Count);
        Assert.False((await db.Listings.SingleAsync()).HasDraft);
        Assert.All(await db.ListingPushes.ToListAsync(), p => Assert.Equal(ListingPushStatus.Succeeded, p.Status));
    }
}
