using System.Text.Json;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ChallengerM4Tests
{
    private const int OtherShopId = 2;
    private const long OtherEtsyShopId = 43;

    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        listing["tags"] = new JsonArray("vase", "pla");
        listing["materials"] = new JsonArray("PLA");
        listing["item_length"] = 10.0m;
        listing["item_width"] = 5.0m;
        listing["item_height"] = 2.0m;
        listing["item_dimensions_unit"] = "in";
        listing["item_weight"] = 1.5m;
        listing["item_weight_unit"] = "lb";
        listing["description"] = "Original Etsy description";
        shop.Listings.Add(listing);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var store = shop.Provider.GetRequiredService<ImageStore>();
        await using var db = shop.Db.CreateDbContext();
        foreach (var image in await db.ListingImages.ToListAsync())
        {
            image.FileName = await store.SaveAsync([1, 2, (byte)image.Rank], ".jpg");
        }

        // Add second shop for cross-shop cloner testing
        db.Shops.Add(new Shop
        {
            Id = OtherShopId,
            EtsyShopId = OtherEtsyShopId,
            EtsyUserId = 222,
            Name = "OtherShop",
            Url = "https://www.etsy.com/shop/OtherShop",
            CurrencyCode = "USD",
            ConnectedAtUtc = shop.Time.Now.UtcDateTime,
            Token = new ShopToken { EncryptedAccessToken = "x", EncryptedRefreshToken = "x", Scopes = "listings_w" },
        });

        db.ShippingProfiles.Add(new ShippingProfile { ShopId = OtherShopId, EtsyShippingProfileId = 502, Title = "Shipping 2" });
        db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = OtherShopId, ReadinessStateId = 602, ReadinessState = "ready_to_ship", DisplayLabel = "Processing 2" });
        db.ReturnPolicies.Add(new ReturnPolicy { ShopId = OtherShopId, EtsyReturnPolicyId = 702, AcceptsReturns = true });
        db.ShopSections.Add(new ShopSection { ShopId = OtherShopId, EtsySectionId = 802, Title = "Section 2" });

        await db.SaveChangesAsync();

        var tokens = (InMemoryTokenStore)shop.Provider.GetRequiredService<IEtsyTokenStore>();
        tokens.Tokens[OtherShopId] = tokens.Tokens[FakeEtsyShop.ShopId] with { AccessToken = "222.access" };

        var localListing = await db.Listings.SingleAsync(l => l.EtsyListingId == 1);
        return (shop, localListing.Id);
    }

    private static TextBlockService Blocks(FakeEtsyShop s) => s.Provider.GetRequiredService<TextBlockService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();
    private static ListingCloner Cloner(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingCloner>();

    private static async Task<string> DraftDescriptionAsync(FakeEtsyShop s, int id) =>
        (string)(await Editor(s).LoadAsync(id))!.Model.Local("description")!;

    [Fact]
    public async Task Dynamic_rerendering_on_individual_and_multiple_field_changes()
    {
        var (shop, listingId) = await SetupAsync();
        using var _ = shop;

        const string templateSource =
            "Shop: {{shop.name}}\n" +
            "Title: {{listing.title}}\n" +
            "Tags: {{listing.tags}}\n" +
            "Materials: {{listing.materials}}\n" +
            "Dimensions: {{listing.dimensions}}\n" +
            "Weight: {{listing.weight}} {{listing.weight_unit}}";

        // Save description with built-in placeholders
        var unknown = await Blocks(shop).SaveDescriptionAsync(listingId, templateSource, "challenger");
        Assert.Empty(unknown);

        // Verify initial rendered draft
        var initialExpected =
            "Shop: TestShop\n" +
            "Title: Galaxy vase\n" +
            "Tags: vase, pla\n" +
            "Materials: PLA\n" +
            "Dimensions: 10 × 5 × 2 in\n" +
            "Weight: 1.5 lb";
        Assert.Equal(initialExpected, await DraftDescriptionAsync(shop, listingId));

        // 1. Dynamic re-rendering on Title change
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["title"] = "Supernova Vase",
        }, "challenger");

        Assert.Equal(
            "Shop: TestShop\n" +
            "Title: Supernova Vase\n" +
            "Tags: vase, pla\n" +
            "Materials: PLA\n" +
            "Dimensions: 10 × 5 × 2 in\n" +
            "Weight: 1.5 lb",
            await DraftDescriptionAsync(shop, listingId));

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleAsync(d => d.ListingId == listingId);
            Assert.Equal(templateSource, row.Source); // Source completely unchanged
        }

        // 2. Dynamic re-rendering on Tags change
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["tags"] = new JsonArray("astronomy", "decor", "galaxy"),
        }, "challenger");

        Assert.Equal(
            "Shop: TestShop\n" +
            "Title: Supernova Vase\n" +
            "Tags: astronomy, decor, galaxy\n" +
            "Materials: PLA\n" +
            "Dimensions: 10 × 5 × 2 in\n" +
            "Weight: 1.5 lb",
            await DraftDescriptionAsync(shop, listingId));

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleAsync(d => d.ListingId == listingId);
            Assert.Equal(templateSource, row.Source); // Source completely unchanged
        }

        // 3. Dynamic re-rendering on Materials change
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["materials"] = new JsonArray("PETG", "Carbon Fiber"),
        }, "challenger");

        Assert.Equal(
            "Shop: TestShop\n" +
            "Title: Supernova Vase\n" +
            "Tags: astronomy, decor, galaxy\n" +
            "Materials: PETG, Carbon Fiber\n" +
            "Dimensions: 10 × 5 × 2 in\n" +
            "Weight: 1.5 lb",
            await DraftDescriptionAsync(shop, listingId));

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleAsync(d => d.ListingId == listingId);
            Assert.Equal(templateSource, row.Source); // Source completely unchanged
        }

        // 4. Dynamic re-rendering on Item Dimensions change
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["item_length"] = 25.5m,
            ["item_width"] = 12.0m,
            ["item_height"] = 8.25m,
            ["item_dimensions_unit"] = "cm",
        }, "challenger");

        Assert.Equal(
            "Shop: TestShop\n" +
            "Title: Supernova Vase\n" +
            "Tags: astronomy, decor, galaxy\n" +
            "Materials: PETG, Carbon Fiber\n" +
            "Dimensions: 25.5 × 12 × 8.25 cm\n" +
            "Weight: 1.5 lb",
            await DraftDescriptionAsync(shop, listingId));

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleAsync(d => d.ListingId == listingId);
            Assert.Equal(templateSource, row.Source); // Source completely unchanged
        }

        // 5. Dynamic re-rendering on Weight change
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["item_weight"] = 350m,
            ["item_weight_unit"] = "g",
        }, "challenger");

        Assert.Equal(
            "Shop: TestShop\n" +
            "Title: Supernova Vase\n" +
            "Tags: astronomy, decor, galaxy\n" +
            "Materials: PETG, Carbon Fiber\n" +
            "Dimensions: 25.5 × 12 × 8.25 cm\n" +
            "Weight: 350 g",
            await DraftDescriptionAsync(shop, listingId));

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleAsync(d => d.ListingId == listingId);
            Assert.Equal(templateSource, row.Source); // Source completely unchanged
        }

        // 6. Dynamic re-rendering when MULTIPLE fields change simultaneously
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["title"] = "Hypernova Centerpiece",
            ["tags"] = new JsonArray("modern", "centerpiece"),
            ["materials"] = new JsonArray("Resin"),
            ["item_length"] = 30m,
            ["item_width"] = 20m,
            ["item_height"] = 15m,
            ["item_dimensions_unit"] = "mm",
            ["item_weight"] = 500m,
            ["item_weight_unit"] = "g",
        }, "challenger");

        Assert.Equal(
            "Shop: TestShop\n" +
            "Title: Hypernova Centerpiece\n" +
            "Tags: modern, centerpiece\n" +
            "Materials: Resin\n" +
            "Dimensions: 30 × 20 × 15 mm\n" +
            "Weight: 500 g",
            await DraftDescriptionAsync(shop, listingId));

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleAsync(d => d.ListingId == listingId);
            Assert.Equal(templateSource, row.Source); // Source completely unchanged
            Assert.Equal(await DraftDescriptionAsync(shop, listingId), row.Rendered);
        }
    }

    [Fact]
    public async Task Preserving_ListingDescription_when_ONLY_built_in_placeholders_are_used_and_pruning_plain_text()
    {
        var (shop, listingId) = await SetupAsync();
        using var _ = shop;

        // ONLY built-in placeholders (no {{text.*}} blocks)
        const string builtinOnly = "Shop: {{shop.name}}\nItem: {{listing.title}}\nMade with: {{listing.materials}}";
        var unknown = await Blocks(shop).SaveDescriptionAsync(listingId, builtinOnly, "challenger");
        Assert.Empty(unknown);

        // Verify row exists in database and has correct Source and Rendered
        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleOrDefaultAsync(d => d.ListingId == listingId);
            Assert.NotNull(row);
            Assert.Equal(builtinOnly, row.Source);
            Assert.Equal("Shop: TestShop\nItem: Galaxy vase\nMade with: PLA", row.Rendered);
        }

        // Now save another field (title) — verify ListingDescription row is STILL preserved and NOT deleted
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["title"] = "Updated Galaxy Vase",
        }, "challenger");

        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleOrDefaultAsync(d => d.ListingId == listingId);
            Assert.NotNull(row);
            Assert.Equal(builtinOnly, row.Source);
            Assert.Equal("Shop: TestShop\nItem: Updated Galaxy Vase\nMade with: PLA", row.Rendered);
        }

        // Now save description as pure plain text WITHOUT any placeholders
        const string plainText = "This is a completely plain description without any placeholders.";
        var unknownPlain = await Blocks(shop).SaveDescriptionAsync(listingId, plainText, "challenger");
        Assert.Empty(unknownPlain);

        // Verify the ListingDescription row IS now pruned/deleted from database
        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleOrDefaultAsync(d => d.ListingId == listingId);
            Assert.Null(row);
        }

        // And draft description holds the plain text
        Assert.Equal(plainText, await DraftDescriptionAsync(shop, listingId));
    }

    [Fact]
    public async Task Cloner_cross_shop_resolves_target_shop_and_title_while_preserving_source()
    {
        var (shop, sourceListingId) = await SetupAsync();
        using var _ = shop;

        const string sourceTemplate =
            "Welcome to {{shop.name}}!\n" +
            "Visit our shop at {{shop.url}}.\n" +
            "Item: {{listing.title}}\n" +
            "Materials: {{listing.materials}}\n" +
            "Dimensions: {{listing.dimensions}}";

        await Blocks(shop).SaveDescriptionAsync(sourceListingId, sourceTemplate, "challenger");

        // Prepare clone settings to OtherShop
        var choice = await Cloner(shop).DefaultsAsync(sourceListingId, OtherShopId);
        choice = choice with
        {
            Title = "Orion Vase for OtherShop",
            ShippingProfileId = 502,
            ReadinessStateId = 602,
            ReturnPolicyId = 702,
            ShopSectionId = 802,
        };

        var (cloneId, blockers) = await Cloner(shop).RequestAsync(sourceListingId, choice, "challenger");
        Assert.Empty(blockers);
        Assert.NotNull(cloneId);

        // Run clone job
        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = JsonSerializer.Serialize(new ClonePayload(cloneId.Value), JobJson.Options),
        }, CancellationToken.None);

        // 1. Verify Etsy createDraftListing request had target shop name, target shop url, and target title
        var createWrite = shop.Writes.First(w => w.Method == "POST" && w.Path.Contains($"/shops/{OtherEtsyShopId}/listings"));
        var query = System.Web.HttpUtility.ParseQueryString(createWrite.Body);
        var etsyDescription = query["description"];
        Assert.NotNull(etsyDescription);

        var expectedTargetRendered =
            "Welcome to OtherShop!\n" +
            "Visit our shop at https://www.etsy.com/shop/OtherShop.\n" +
            "Item: Orion Vase for OtherShop\n" +
            "Materials: PLA\n" +
            "Dimensions: 10 × 5 × 2 in";
        Assert.Equal(expectedTargetRendered, etsyDescription);

        // 2. Verify cloned listing in DB has a ListingDescription row with the original Source template
        await using var checkDb = shop.Db.CreateDbContext();
        var cloneRecord = await checkDb.ListingClones.SingleAsync(c => c.Id == cloneId.Value);
        Assert.Equal(ListingCloneStatus.Succeeded, cloneRecord.Status);
        Assert.NotNull(cloneRecord.NewListingId);

        var newListingDesc = await checkDb.ListingDescriptions.SingleOrDefaultAsync(d => d.ListingId == cloneRecord.NewListingId);
        Assert.NotNull(newListingDesc);
        Assert.Equal(sourceTemplate, newListingDesc.Source); // Exact raw source preserved!
        Assert.Equal(expectedTargetRendered, newListingDesc.Rendered); // Target rendered text
    }

    [Fact]
    public async Task Edge_cases_and_resilience_partial_dimensions_empty_fields_case_insensitivity()
    {
        var (shop, listingId) = await SetupAsync();
        using var _ = shop;

        // Partial dimensions (missing height): {{listing.dimensions}} should resolve to empty string ""
        await Editor(shop).SaveAsync(listingId, new Dictionary<string, JsonNode?>
        {
            ["item_length"] = 10m,
            ["item_width"] = 5m,
            ["item_height"] = null,
            ["materials"] = new JsonArray(), // empty materials
            ["tags"] = new JsonArray(), // empty tags
        }, "challenger");

        const string edgeTemplate =
            "Title: {{LISTING.TITLE}}\n" +
            "Shop: {{SHOP.NAME}}\n" +
            "Dims: [{{listing.DIMENSIONS}}]\n" +
            "Mats: [{{listing.MATERIAL}}]\n" +
            "Tags: [{{listing.TAGS}}]";

        var unknown = await Blocks(shop).SaveDescriptionAsync(listingId, edgeTemplate, "challenger");
        Assert.Empty(unknown);

        // Case-insensitivity and empty field resolution
        var expected =
            "Title: Galaxy vase\n" +
            "Shop: TestShop\n" +
            "Dims: []\n" +
            "Mats: []\n" +
            "Tags: []";

        Assert.Equal(expected, await DraftDescriptionAsync(shop, listingId));

        // Push preview must NOT be blocked when optional fields resolve to empty
        var preview = await Editor(shop).PreviewAsync(listingId);
        Assert.DoesNotContain(preview.Blockers, b => b.Contains("unresolved"));
    }

    [Fact]
    public async Task Nested_templates_and_cycle_detection_adversarial_challenge()
    {
        var (shop, listingId) = await SetupAsync();
        using var _ = shop;

        // Create modular templates:
        // inner -> "Created by {{shop.name}}"
        // outer -> "Description: {{listing.title}}\n{{text.inner}}"
        var (innerId, errInner) = await Blocks(shop).SaveAsync(null, "Inner Block", "inner", "Created by {{shop.name}}", "challenger");
        Assert.Null(errInner);
        Assert.NotNull(innerId);

        var (outerId, errOuter) = await Blocks(shop).SaveAsync(null, "Outer Block", "outer", "Description: {{listing.title}}\n{{text.inner}}", "challenger");
        Assert.Null(errOuter);
        Assert.NotNull(outerId);

        var unknown = await Blocks(shop).SaveDescriptionAsync(listingId, "Header\n{{text.outer}}\nFooter", "challenger");
        Assert.Empty(unknown);

        Assert.Equal(
            "Header\nDescription: Galaxy vase\nCreated by TestShop\nFooter",
            await DraftDescriptionAsync(shop, listingId));

        // Now test cycle detection: attempt to make inner reference outer
        var (_, cycleErr) = await Blocks(shop).SaveAsync(innerId, "Inner Block", "inner", "Cycle test {{text.outer}}", "challenger");
        Assert.Equal("Template contains circular template references.", cycleErr);

        // Verify 3-node cycle: A -> B -> C -> A
        var (idA, _) = await Blocks(shop).SaveAsync(null, "Block A", "node-a", "A refers to {{text.node-b}}", "challenger");
        var (idB, _) = await Blocks(shop).SaveAsync(null, "Block B", "node-b", "B refers to {{text.node-c}}", "challenger");
        var (idC, errC) = await Blocks(shop).SaveAsync(null, "Block C", "node-c", "C refers to {{text.node-a}}", "challenger");
        Assert.Null(idC);
        Assert.Equal("Template contains circular template references.", errC);
    }
}
