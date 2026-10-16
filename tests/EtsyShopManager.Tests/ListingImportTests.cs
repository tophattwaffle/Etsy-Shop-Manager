using System.Text.Json;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ListingMapperTests
{
    private static EtsyListing Parse(System.Text.Json.Nodes.JsonObject json) =>
        json.Deserialize<EtsyListing>(EtsyJson.Options)!;

    [Fact]
    public void Maps_fields_money_and_timestamps()
    {
        var src = Parse(FakeEtsyShop.Listing(7, "Galaxy vase", quantity: 3));
        var listing = new Listing();

        ListingMapper.ApplyFields(listing, src);

        Assert.Equal(7, listing.EtsyListingId);
        Assert.Equal("Galaxy vase", listing.Title);
        Assert.Equal(1999, listing.PriceAmount);
        Assert.Equal(100, listing.PriceDivisor);
        Assert.Equal("USD", listing.CurrencyCode);
        Assert.Equal(3, listing.Quantity);
        Assert.Equal(["vase", "pla"], listing.Tags);
        Assert.Equal(["SKU7-S", "SKU7-L"], listing.Skus);
        Assert.Equal(501, listing.ShippingProfileId);
        Assert.Equal(601, listing.ReadinessStateId);
        Assert.Null(listing.FeaturedRank); // Etsy sends -1 for "not featured"
        Assert.Null(listing.PersonalizationJson); // no questions
        Assert.Equal(DateTime.UnixEpoch.AddSeconds(1_760_000_000), listing.EtsyUpdatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, listing.EtsyUpdatedAtUtc!.Value.Kind);
    }

    [Fact]
    public void Inventory_sync_matches_by_Etsy_id_and_keeps_rows()
    {
        var listing = new Listing();
        ListingMapper.SyncInventory(listing, Parse(FakeEtsyShop.Listing(7, "x")).Inventory!);
        var small = listing.Products.Single(p => p.Sku == "SKU7-S");
        var offering = small.Offerings.Single();

        var updated = Parse(FakeEtsyShop.Listing(7, "x", quantity: 9));
        ListingMapper.SyncInventory(listing, updated.Inventory!);

        Assert.Equal(2, listing.Products.Count);
        Assert.Same(small, listing.Products.Single(p => p.Sku == "SKU7-S"));
        Assert.Same(offering, small.Offerings.Single());
        Assert.Equal(9, offering.Quantity);
        Assert.Equal("Small", small.PropertyValues.Single().Values.Single());
        Assert.Equal([100L], listing.PriceOnProperty);
    }

    [Fact]
    public void Image_sync_clears_stored_file_when_source_changes_and_removes_deleted_images()
    {
        var listing = new Listing();
        ListingMapper.SyncImages(listing, Parse(FakeEtsyShop.Listing(7, "x")).Images!);
        listing.Images.ForEach(i => i.FileName = new string('a', 64) + ".jpg");

        var images = Parse(FakeEtsyShop.Listing(7, "x")).Images!;
        images.RemoveAt(1);
        images[0].UrlFullxFull = "https://i.etsystatic.com/new/il_fullxfull.jpg";
        ListingMapper.SyncImages(listing, images);

        var image = Assert.Single(listing.Images);
        Assert.Equal(71, image.EtsyImageId);
        Assert.Null(image.FileName);
    }
}

public class ManagedListingStateTests
{
    private static Listing Build(Action<System.Text.Json.Nodes.JsonObject>? change = null)
    {
        var json = FakeEtsyShop.Listing(7, "Galaxy vase");
        change?.Invoke(json);
        var src = json.Deserialize<EtsyListing>(EtsyJson.Options)!;
        var listing = new Listing();
        ListingMapper.ApplyFields(listing, src);
        ListingMapper.SyncImages(listing, src.Images!);
        ListingMapper.SyncInventory(listing, src.Inventory!);
        return listing;
    }

    private static string HashOf(Listing l) => ManagedListingState.Hash(ManagedListingState.From(l).ToCanonicalJson());

    [Fact]
    public void Hash_is_stable_and_ignores_quantity_and_product_ids()
    {
        var a = Build();
        var b = Build(j =>
        {
            j["quantity"] = 999;
            var products = j["inventory"]!["products"]!.AsArray();
            foreach (var p in products)
            {
                p!["product_id"] = (long)p["product_id"]! + 5000;
                p["offerings"]![0]!["offering_id"] = 1;
                p["offerings"]![0]!["quantity"] = 999;
            }
            // Etsy can return products in a different order.
            var first = products[0]!;
            products.RemoveAt(0);
            products.Add(first);
        });

        Assert.Equal(HashOf(a), HashOf(b));
        Assert.Equal(64, HashOf(a).Length);
    }

    [Fact]
    public void Personalization_questions_are_part_of_the_state()
    {
        var with = Build(j => j["personalization"] = new System.Text.Json.Nodes.JsonObject
        {
            ["personalization_questions"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject
            {
                ["question_text"] = "Name", ["required"] = true,
            }),
        });

        Assert.NotNull(with.PersonalizationJson);
        Assert.Contains("\"question_text\":\"Name\"", ManagedListingState.From(with).ToCanonicalJson());
        Assert.NotEqual(HashOf(Build()), HashOf(with));
    }

    [Fact]
    public void Sold_out_counts_as_active()
    {
        Assert.Equal(HashOf(Build()), HashOf(Build(j => j["state"] = "sold_out")));
        Assert.NotEqual(HashOf(Build()), HashOf(Build(j => j["state"] = "inactive")));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("price")]
    [InlineData("tags")]
    [InlineData("alt_text")]
    [InlineData("offering_price")]
    public void Managed_field_change_changes_hash(string field)
    {
        var changed = Build(j =>
        {
            switch (field)
            {
                case "title": j["title"] = "Other"; break;
                case "price": j["price"] = FakeEtsyShop.Money(2500); break;
                case "tags": j["tags"] = new System.Text.Json.Nodes.JsonArray("vase"); break;
                case "alt_text": j["images"]![0]!["alt_text"] = "New alt"; break;
                case "offering_price": j["inventory"]!["products"]![0]!["offerings"]![0]!["price"] = FakeEtsyShop.Money(1); break;
            }
        });
        Assert.NotEqual(HashOf(Build()), HashOf(changed));
    }
}

public class ListingImporterTests
{
    [Fact]
    public async Task Imports_every_state_with_paging_lookups_and_attributes()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.AddRange([
            FakeEtsyShop.Listing(1, "One"), FakeEtsyShop.Listing(2, "Two"), FakeEtsyShop.Listing(3, "Three"),
            FakeEtsyShop.Listing(4, "Draft", state: "draft"), FakeEtsyShop.Listing(5, "Off", state: "inactive"),
        ]);
        shop.Importer.PageSize = 2;

        var result = await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, force: false, CancellationToken.None);

        Assert.Equal(5, result.ListingsSeen);
        var listingCalls = shop.RequestedPaths.Where(p => p.StartsWith("/v3/application/shops/42/listings?")).ToList();
        Assert.Contains(listingCalls, p => p.Contains("state=active") && p.Contains("offset=0"));
        Assert.Contains(listingCalls, p => p.Contains("state=active") && p.Contains("offset=2"));
        Assert.DoesNotContain(listingCalls, p => p.Contains("state=active") && p.Contains("offset=4"));
        foreach (var state in EtsyListingStates.All) Assert.Contains(listingCalls, p => p.Contains($"state={state}"));
        Assert.All(listingCalls, p => Assert.Contains("includes=Images,Inventory,Personalization", Uri.UnescapeDataString(p)));

        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(5, await db.Listings.CountAsync());
        Assert.Equal(10, await db.ListingImages.CountAsync());
        Assert.Equal(10, await db.ListingProducts.CountAsync());
        Assert.Equal(10, await db.ListingOfferings.CountAsync());
        Assert.Equal("Standard", (await db.ShippingProfiles.SingleAsync()).Title);
        Assert.Equal("1-3 days", (await db.ProcessingProfiles.SingleAsync()).DisplayLabel);
        Assert.Equal(30, (await db.ReturnPolicies.SingleAsync()).ReturnDeadlineDays);
        Assert.Equal("Vases", (await db.ShopSections.SingleAsync()).Title);

        var listing = await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.EtsyListingId == 4);
        Assert.Equal("draft", listing.State);
        Assert.Equal("Black", listing.Attributes.Single().Values.Single());
        Assert.Contains("\"listing_id\":4", listing.EtsyState!.RawJson);
        Assert.Equal(ManagedListingState.Hash(listing.EtsyState.ManagedJson), listing.EtsyState.ManagedHash);
        Assert.DoesNotContain("\"quantity\":", listing.EtsyState.ManagedJson);

        var sync = await db.ShopSyncStates.SingleAsync();
        Assert.False(sync.IsRunOpen);
        Assert.Equal(5, sync.LastListingCount);
    }

    [Fact]
    public async Task Reimport_is_idempotent_and_skips_attributes_for_unchanged_listings()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.AddRange([FakeEtsyShop.Listing(1, "One"), FakeEtsyShop.Listing(2, "Two")]);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        Dictionary<long, (int Id, string Hash, DateTime Changed)> before;
        List<int> productIds, imageIds;
        await using (var db = shop.Db.CreateDbContext())
        {
            before = await db.Listings.Include(l => l.EtsyState)
                .ToDictionaryAsync(l => l.EtsyListingId, l => (l.Id, l.EtsyState!.ManagedHash, l.EtsyState.ManagedChangedAtUtc));
            productIds = await db.ListingProducts.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
            imageIds = await db.ListingImages.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        }

        shop.Api.Requests.Clear();
        shop.Time.Now = shop.Time.Now.AddDays(1);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        Assert.DoesNotContain(shop.RequestedPaths, p => p.EndsWith("/properties"));
        await using (var db = shop.Db.CreateDbContext())
        {
            var after = await db.Listings.Include(l => l.EtsyState)
                .ToDictionaryAsync(l => l.EtsyListingId, l => (l.Id, l.EtsyState!.ManagedHash, l.EtsyState.ManagedChangedAtUtc));
            Assert.Equal(before, after);
            Assert.Equal(productIds, await db.ListingProducts.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync());
            Assert.Equal(imageIds, await db.ListingImages.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync());
            Assert.All(await db.Listings.ToListAsync(), l => Assert.Equal(shop.Time.Now.UtcDateTime, l.LastImportedAtUtc));
        }
    }

    [Fact]
    public async Task Changed_listing_gets_new_hash_and_attributes_refetched()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "One"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        string oldHash;
        await using (var db = shop.Db.CreateDbContext())
            oldHash = (await db.ListingEtsyStates.SingleAsync()).ManagedHash;

        shop.Listings[0] = FakeEtsyShop.Listing(1, "One, renamed", updated: 1_760_100_000);
        shop.Api.Requests.Clear();
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        Assert.Contains(shop.RequestedPaths, p => p.EndsWith("/listings/1/properties"));
        await using (var db = shop.Db.CreateDbContext())
        {
            var state = await db.ListingEtsyStates.SingleAsync();
            Assert.NotEqual(oldHash, state.ManagedHash);
            Assert.Contains("One, renamed", state.ManagedJson);
        }
    }

    [Fact]
    public async Task Listing_gone_from_Etsy_is_flagged_missing_not_deleted()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.AddRange([FakeEtsyShop.Listing(1, "One"), FakeEtsyShop.Listing(2, "Two")]);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        shop.Listings.RemoveAt(1);
        var result = await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        Assert.Equal(1, result.MarkedMissing);
        await using var db = shop.Db.CreateDbContext();
        Assert.True((await db.Listings.SingleAsync(l => l.EtsyListingId == 2)).IsMissingOnEtsy);
        Assert.False((await db.Listings.SingleAsync(l => l.EtsyListingId == 1)).IsMissingOnEtsy);
    }

    [Fact]
    public async Task Interrupted_import_resumes_from_the_saved_page()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.AddRange([
            FakeEtsyShop.Listing(1, "One"), FakeEtsyShop.Listing(2, "Two"), FakeEtsyShop.Listing(3, "Three"),
            FakeEtsyShop.Listing(4, "Draft", state: "draft"),
        ]);
        shop.Importer.PageSize = 2;
        shop.FailingStates.Add("draft");

        await Assert.ThrowsAsync<EtsyApiException>(() =>
            shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None));

        await using (var db = shop.Db.CreateDbContext())
        {
            Assert.True((await db.ShopSyncStates.SingleAsync()).IsRunOpen);
            Assert.Equal(3, await db.Listings.CountAsync());
        }

        shop.FailingStates.Clear();
        shop.Api.Requests.Clear();
        var result = await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        Assert.True(result.Resumed);
        Assert.DoesNotContain(shop.RequestedPaths, p => p.Contains("state=active"));
        Assert.DoesNotContain(shop.RequestedPaths, p => p.Contains("shipping-profiles"));
        await using (var db = shop.Db.CreateDbContext())
        {
            Assert.Equal(4, await db.Listings.CountAsync());
            // Listings seen before the interruption belong to the same run, so none is flagged missing.
            Assert.Equal(0, await db.Listings.CountAsync(l => l.IsMissingOnEtsy));
        }
    }

    [Fact]
    public async Task Listings_copied_on_Etsy_can_share_image_ids()
    {
        // Etsy reuses listing_image_id when a listing is copied; seen on a real shop (active + sold_out copy).
        using var shop = new FakeEtsyShop();
        var copy = FakeEtsyShop.Listing(2, "Copy", state: "sold_out");
        copy["images"] = FakeEtsyShop.Images(1);
        shop.Listings.AddRange([FakeEtsyShop.Listing(1, "Original"), copy]);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(4, await db.ListingImages.CountAsync());
        Assert.Equal(2, await db.ListingImages.CountAsync(i => i.EtsyImageId == 11));
        var downloaded = await shop.Downloader.DownloadForShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        Assert.Equal(0, downloaded.Failed);
    }

    [Fact]
    public async Task Missing_includes_fall_back_to_per_listing_calls()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "One", withImages: false, withInventory: false));

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        Assert.Contains(shop.RequestedPaths, p => p == "/v3/application/listings/1/images");
        Assert.Contains(shop.RequestedPaths, p => p == "/v3/application/listings/1/inventory");
        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(2, await db.ListingImages.CountAsync());
        Assert.Equal(2, await db.ListingProducts.CountAsync());
    }

    [Fact]
    public async Task Search_finds_by_title_sku_tag_and_listing_id()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.AddRange([FakeEtsyShop.Listing(1, "Galaxy vase"), FakeEtsyShop.Listing(2, "Plant pot", state: "draft")]);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        var queries = shop.Provider.GetRequiredService<ListingQueries>();

        async Task<List<long>> Find(ListingFilter f) => (await queries.SearchAsync(f)).Rows.Select(r => r.EtsyListingId).ToList();

        Assert.Equal([1L], await Find(new ListingFilter { Text = "galaxy" }));
        Assert.Equal([2L], await Find(new ListingFilter { Text = "SKU2-L" }));
        Assert.Equal([2L], await Find(new ListingFilter { Text = "2" }));
        Assert.Equal(2, (await Find(new ListingFilter { Text = "pla" })).Count); // tag
        Assert.Equal([2L], await Find(new ListingFilter { States = ["draft"] }));
        Assert.Empty(await Find(new ListingFilter { Text = "100%" }));
    }
}

public class TaxonomyImporterTests
{
    [Fact]
    public async Task Imports_tree_with_paths_and_properties_for_used_taxonomies()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "One"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await shop.Taxonomy.RefreshAsync(force: false, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            var vases = await db.TaxonomyNodes.SingleAsync(n => n.Id == 1001);
            Assert.Equal("Home & Living > Vases", vases.FullPath);
            Assert.Equal(1, vases.ParentId);
            Assert.True((await db.TaxonomyNodes.SingleAsync(n => n.Id == 1)).HasChildren);
            var prop = await db.TaxonomyProperties.SingleAsync();
            Assert.Equal((1001L, 200L), (prop.TaxonomyId, prop.PropertyId));
            Assert.Equal(1, prop.PossibleValueCount);
        }

        // Fresh data: a second refresh makes no calls.
        shop.Api.Requests.Clear();
        await shop.Taxonomy.RefreshAsync(force: false, CancellationToken.None);
        Assert.Empty(shop.Api.Requests);
    }
}

public class ImageStoreTests
{
    [Fact]
    public async Task Same_bytes_get_the_same_content_hashed_name()
    {
        using var storage = new TempStorage();
        var store = new ImageStore(storage.Options);
        byte[] bytes = [1, 2, 3, 4];

        var a = await store.SaveAsync(bytes, "jpeg");
        var b = await store.SaveAsync(bytes, "jpg");

        Assert.Equal(a, b);
        Assert.Equal("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a.jpg", a);
        Assert.Equal(Path.Combine(storage.Options.ImagesDirectory, "9f", a), store.PathFor(a));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(store.PathFor(a)));
        Assert.NotEqual(a, await store.SaveAsync([1, 2, 3, 5], "jpg"));
    }

    [Theory]
    [InlineData("image/png", "https://x/a.jpg", "png")]
    [InlineData("application/octet-stream", "https://x/a.webp?v=1", "webp")]
    [InlineData(null, "https://x/a", "jpg")]
    public void Picks_extension(string? contentType, string url, string expected) =>
        Assert.Equal(expected, ImageStore.ExtensionFor(contentType, url));

    [Theory]
    [InlineData("../etsyshopmanager.db")]
    [InlineData("9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a.exe")]
    [InlineData("9F64A747E1B97F131FABB6B447296C9B6F0201E79FB3C5356E6C77E89B6A806A.jpg")]
    [InlineData(null)]
    public void Rejects_names_that_are_not_content_hashes(string? name)
    {
        Assert.False(ImageStore.IsValidFileName(name));
        Assert.Null(ImageStore.UrlFor(name));
    }

    [Fact]
    public async Task Deletes_only_old_unreferenced_files()
    {
        using var storage = new TempStorage();
        var store = new ImageStore(storage.Options);
        var keep = await store.SaveAsync([1], "jpg");
        var orphan = await store.SaveAsync([2], "jpg");
        var fresh = await store.SaveAsync([3], "jpg");
        var old = DateTime.UtcNow.AddDays(-3);
        File.SetLastWriteTimeUtc(store.PathFor(keep), old);
        File.SetLastWriteTimeUtc(store.PathFor(orphan), old);

        var deleted = store.DeleteUnreferenced(new HashSet<string> { keep }, TimeSpan.FromDays(1), DateTime.UtcNow);

        Assert.Equal(1, deleted);
        Assert.True(store.Exists(keep));
        Assert.False(store.Exists(orphan));
        Assert.True(store.Exists(fresh));
    }
}

public class ImageDownloaderTests
{
    [Fact]
    public async Task Downloads_full_and_display_copies_once()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "One"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var first = await shop.Downloader.DownloadForShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        Assert.Equal(new ImageDownloadResult(2, 0, 2), first);
        Assert.Equal(4, shop.Cdn.Requests.Count); // full + 570px per image
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var image in await db.ListingImages.ToListAsync())
            {
                Assert.True(shop.Store.Exists(image.FileName));
                Assert.True(shop.Store.Exists(image.ThumbFileName));
                Assert.NotEqual(image.FileName, image.ThumbFileName);
                Assert.NotNull(image.DownloadedAtUtc);
            }
        }

        var second = await shop.Downloader.DownloadForShopAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        Assert.Equal(0, second.Pending);
        Assert.Equal(4, shop.Cdn.Requests.Count);
    }
}
