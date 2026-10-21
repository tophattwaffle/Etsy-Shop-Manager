using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Shops;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class NewListingTests
{
    private const int OtherShopId = 2;
    private const long OtherEtsyShopId = 43;

    private sealed class TestAuditLog : IAuditLog
    {
        public List<(string Action, string? EntityType, string? EntityId, string? Details, string? User)> Entries { get; } = [];

        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
            string? userName = null, CancellationToken ct = default)
        {
            Entries.Add((action, entityType, entityId, details, userName));
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task ShopDefaults_can_be_saved_and_retrieved()
    {
        var settings = new InMemorySettings();
        var audit = new TestAuditLog();
        var service = new ShopDefaultsService(settings, audit);

        // Before configuration, returns null
        var empty = await service.GetAsync(1);
        Assert.Null(empty);

        var defaults = new ShopDefaults(
            ShippingProfileId: 501,
            ReadinessStateId: 601,
            ReturnPolicyId: 701,
            ShopSectionId: 801,
            WhoMade: "i_did",
            WhenMade: "made_to_order",
            TaxonomyId: 12345);

        await service.SaveAsync(1, defaults, "adminUser");

        var loaded = await service.GetAsync(1);
        Assert.NotNull(loaded);
        Assert.Equal(501, loaded.ShippingProfileId);
        Assert.Equal(601, loaded.ReadinessStateId);
        Assert.Equal(701, loaded.ReturnPolicyId);
        Assert.Equal(801, loaded.ShopSectionId);
        Assert.Equal("i_did", loaded.WhoMade);
        Assert.Equal("made_to_order", loaded.WhenMade);
        Assert.Equal(12345, loaded.TaxonomyId);

        // Audit entry recorded
        Assert.Single(audit.Entries);
        var entry = audit.Entries[0];
        Assert.Equal("shop.defaults-updated", entry.Action);
        Assert.Equal("Shop", entry.EntityType);
        Assert.Equal("1", entry.EntityId);
        Assert.Equal("adminUser", entry.User);
        Assert.Contains("501", entry.Details);

        // Corrupt JSON handled gracefully
        await settings.SetAsync(ShopDefaultsService.SettingKey(2), "{ invalid json }");
        var corrupt = await service.GetAsync(2);
        Assert.Null(corrupt);
    }

    [Fact]
    public async Task ListingCloner_DefaultsAsync_pre_fills_from_ShopDefaults()
    {
        using var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Original vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        await using (var db = shop.Db.CreateDbContext())
        {
            db.Shops.Add(new Shop
            {
                Id = OtherShopId, EtsyShopId = OtherEtsyShopId, EtsyUserId = 222, Name = "OtherShop", CurrencyCode = "USD",
                ConnectedAtUtc = shop.Time.Now.UtcDateTime,
                Token = new ShopToken { EncryptedAccessToken = "x", EncryptedRefreshToken = "x", Scopes = "listings_w" },
            });
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = OtherShopId, EtsyShippingProfileId = 502, Title = "Shipping 2" });
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = OtherShopId, ReadinessStateId = 602, ReadinessState = "ready_to_ship", DisplayLabel = "Processing 2" });
            db.ReturnPolicies.Add(new ReturnPolicy { ShopId = OtherShopId, EtsyReturnPolicyId = 702, AcceptsReturns = true });
            db.ShopSections.Add(new ShopSection { ShopId = OtherShopId, EtsySectionId = 802, Title = "Section 2" });
            await db.SaveChangesAsync();
        }

        await using (var checkDb = shop.Db.CreateDbContext())
        {
            var sourceListingId = (await checkDb.Listings.SingleAsync()).Id;
            var settings = shop.Provider.GetRequiredService<ISettingsStore>();
            var audit = new TestAuditLog();
            var defaultsService = new ShopDefaultsService(settings, audit);
            var cloner = new ListingCloner(shop.Db, shop.Provider.GetRequiredService<ImageStore>(),
                shop.Provider.GetRequiredService<RecordingJobQueue>(), settings, audit, shop.Time, defaultsService);

            // When no defaults and no last choice, all profiles are null
            var noDefaults = await cloner.DefaultsAsync(sourceListingId, OtherShopId);
            Assert.Null(noDefaults.ShippingProfileId);
            Assert.Null(noDefaults.ReadinessStateId);
            Assert.Null(noDefaults.ReturnPolicyId);
            Assert.Null(noDefaults.ShopSectionId);

            // Configure defaults for OtherShop
            await defaultsService.SaveAsync(OtherShopId, new ShopDefaults(502, 602, 702, 802), "admin");

            var withDefaults = await cloner.DefaultsAsync(sourceListingId, OtherShopId);
            Assert.Equal((502L, 602L, 702L, 802L),
                (withDefaults.ShippingProfileId, withDefaults.ReadinessStateId, withDefaults.ReturnPolicyId, withDefaults.ShopSectionId));

            // When defaults have null return policy / section, falls back to LastChoice if present
            await defaultsService.SaveAsync(OtherShopId, new ShopDefaults(502, 602, null, null), "admin");
            var lastChoice = new CloneSettings(OtherShopId, "Last", 999, 999, 702, 802);
            await settings.SetAsync(ListingCloner.LastChoiceKey(OtherShopId), JsonSerializer.Serialize(lastChoice));

            var hybrid = await cloner.DefaultsAsync(sourceListingId, OtherShopId);
            // Shipping & readiness from ShopDefaults (502, 602); return policy & section from last choice (702, 802)
            Assert.Equal((502L, 602L, 702L, 802L),
                (hybrid.ShippingProfileId, hybrid.ReadinessStateId, hybrid.ReturnPolicyId, hybrid.ShopSectionId));
        }
    }

    [Fact]
    public void Input_validation_rejects_invalid_inputs()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), shop.Provider.GetRequiredService<IAuditLog>(),
            NullLogger<ListingCreationService>.Instance);

        var valid = new NewListingModel
        {
            ShopId = 1,
            Title = "Handmade Ceramic Mug with Handle",
            Price = 24.99m,
            Quantity = 5,
            TaxonomyId = 100,
            ShippingProfileId = 501,
            ReadinessStateId = 601,
            ReturnPolicyId = 701,
            ShopSectionId = 801,
            Tags = ["ceramic mug", "coffee cup"],
            Materials = ["clay", "ceramic glaze"],
        };
        Assert.Empty(service.Validate(valid));

        // 1. Title too long (> 140 chars)
        var longTitle = valid with { Title = new string('A', 141) };
        var p1 = service.Validate(longTitle);
        Assert.Contains(p1, p => p.Contains("141 characters") && p.Contains("140"));

        // 2. Title with empty or whitespace
        var emptyTitle = valid with { Title = "   " };
        Assert.Contains(service.Validate(emptyTitle), p => p.Contains("can't be empty"));

        // 3. Title with forbidden characters (emoji or unaccepted symbols)
        var emojiTitle = valid with { Title = "Ceramic Mug ☕✨" };
        var p3 = service.Validate(emojiTitle);
        Assert.Contains(p3, p => p.Contains("can't contain"));

        // 4. Title with multiple % : & + symbols (Etsy allows at most once each)
        var multiSymbolTitle = valid with { Title = "Ceramic Mug 50% Off & 20% Discount" };
        var p4 = service.Validate(multiSymbolTitle);
        Assert.Contains(p4, p => p.Contains("uses \"%\" 2 times"));

        // 5. Invalid tags (forbidden characters like @, #, !)
        var invalidTags = valid with { Tags = ["coffee#mug", "tea@cup", "valid tag"] };
        var p5 = service.Validate(invalidTags);
        Assert.Contains(p5, p => p.Contains("coffee#mug"));
        Assert.Contains(p5, p => p.Contains("tea@cup"));

        // 5a. Too many tags (> 13)
        var tooManyTags = valid with { Tags = Enumerable.Range(1, 14).Select(i => $"tag{i}").ToList() };
        var pTagsCount = service.Validate(tooManyTags);
        Assert.Contains(pTagsCount, p => p == $"Etsy allows at most {ListingEditor.MaxTags} tags.");

        // 5b. Tag too long (> 20 chars)
        var longTag = valid with { Tags = ["tag-exceeding-twenty-chars"] };
        var pTagLen = service.Validate(longTag);
        Assert.Contains(pTagLen, p => p == $"The tag \"tag-exceeding-twenty-chars\" is longer than {ListingEditor.MaxTagLength} characters.");

        // 6. Invalid materials (forbidden characters like hyphens or punctuation)
        var invalidMaterials = valid with { Materials = ["stainless-steel", "lead-free glaze!"] };
        var p6 = service.Validate(invalidMaterials);
        Assert.Contains(p6, p => p.Contains("stainless-steel"));
        Assert.Contains(p6, p => p.Contains("lead-free glaze!"));

        // 6a. Too many materials (> 13)
        var tooManyMaterials = valid with { Materials = Enumerable.Range(1, 14).Select(i => $"mat{i}").ToList() };
        var pMatCount = service.Validate(tooManyMaterials);
        Assert.Contains(pMatCount, p => p == $"Etsy allows at most {ListingEditor.MaxMaterials} materials.");

        // 7. Missing required profiles
        var missingShipping = valid with { ShippingProfileId = null };
        Assert.Contains(service.Validate(missingShipping), p => p.Contains("shipping profile"));

        var missingReadiness = valid with { ReadinessStateId = null };
        Assert.Contains(service.Validate(missingReadiness), p => p.Contains("processing profile"));

        // 8. Missing category taxonomy
        var missingTaxonomy = valid with { TaxonomyId = null };
        Assert.Contains(service.Validate(missingTaxonomy), p => p.Contains("category"));

        // 9. Invalid price (<= 0)
        var zeroPrice = valid with { Price = 0m };
        Assert.Contains(service.Validate(zeroPrice), p => p.Contains("Price must be greater than 0"));

        var negativePrice = valid with { Price = -10m };
        Assert.Contains(service.Validate(negativePrice), p => p.Contains("Price must be greater than 0"));

        // 10. Invalid quantity (< 1)
        var zeroQty = valid with { Quantity = 0 };
        Assert.Contains(service.Validate(zeroQty), p => p.Contains("Quantity must be at least 1"));
    }

    [Fact]
    public async Task Draft_creation_posts_to_Etsy_imports_locally_and_never_publishes_as_active()
    {
        using var shop = new FakeEtsyShop();
        var audit = new TestAuditLog();

        await using (var db = shop.Db.CreateDbContext())
        {
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = FakeEtsyShop.ShopId, EtsyShippingProfileId = 501, Title = "Standard Shipping" });
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = FakeEtsyShop.ShopId, ReadinessStateId = 601, ReadinessState = "ready_to_ship", DisplayLabel = "1-3 business days" });
            db.ReturnPolicies.Add(new ReturnPolicy { ShopId = FakeEtsyShop.ShopId, EtsyReturnPolicyId = 701, AcceptsReturns = true });
            db.ShopSections.Add(new ShopSection { ShopId = FakeEtsyShop.ShopId, EtsySectionId = 801, Title = "Pottery" });
            db.TaxonomyNodes.Add(new TaxonomyNode { Id = 1234, Name = "Mugs", FullPath = "Home & Living > Kitchen & Dining > Drinkware > Mugs", ParentId = null });
            await db.SaveChangesAsync();
        }

        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), audit, NullLogger<ListingCreationService>.Instance);

        var model = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "Handmade Speckled Stoneware Mug",
            Price = 32.50m,
            Quantity = 4,
            TaxonomyId = 1234,
            ShippingProfileId = 501,
            ReadinessStateId = 601,
            ReturnPolicyId = 701,
            ShopSectionId = 801,
            WhoMade = "i_did",
            WhenMade = "made_to_order",
            Description = "A wheel-thrown ceramic mug with natural speckled glaze.",
            Tags = ["stoneware mug", "ceramic mug", "coffee lover"],
            Materials = ["clay", "ceramic glaze"],
        };

        var localId = await service.CreateDraftAsync(model, "tophattwaffle");
        Assert.True(localId > 0, "Expected a valid positive local listing ID.");

        // 1. Verify FakeEtsyShop received exactly one POST to /v3/application/shops/{shopId}/listings
        Assert.Single(shop.Writes);
        var write = shop.Writes[0];
        Assert.Equal("POST", write.Method);
        Assert.Equal($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/listings", write.Path);

        // 2. Verify payload sent to Etsy
        var form = HttpUtility.ParseQueryString(write.Body);
        Assert.Equal("Handmade Speckled Stoneware Mug", form["title"]);
        Assert.Equal("32.50", form["price"]);
        Assert.Equal("4", form["quantity"]);
        Assert.Equal("1234", form["taxonomy_id"]);
        Assert.Equal("501", form["shipping_profile_id"]);
        Assert.Equal("601", form["readiness_state_id"]);
        Assert.Equal("701", form["return_policy_id"]);
        Assert.Equal("801", form["shop_section_id"]);
        Assert.Equal("i_did", form["who_made"]);
        Assert.Equal("made_to_order", form["when_made"]);
        Assert.Equal("stoneware mug,ceramic mug,coffee lover", form["tags"]);
        Assert.Equal("clay,ceramic glaze", form["materials"]);
        Assert.Equal("physical", form["type"]);
        Assert.Equal("A wheel-thrown ceramic mug with natural speckled glaze.", form["description"]);

        // 3. Verify that the state is NEVER set to active
        Assert.DoesNotContain(shop.Writes, w => w.Body.Contains("state=active") || w.Body.Contains("active"));

        // 4. Verify local database listing
        await using var verifyDb = shop.Db.CreateDbContext();
        var localListing = await verifyDb.Listings.FirstOrDefaultAsync(l => l.Id == localId);
        Assert.NotNull(localListing);
        Assert.Equal("draft", localListing.State); // State MUST be draft, NEVER active
        Assert.Equal("Handmade Speckled Stoneware Mug", localListing.Title);
        Assert.Equal(4, localListing.Quantity);
        Assert.Equal(FakeEtsyShop.ShopId, localListing.ShopId);
        Assert.Equal(1234L, localListing.TaxonomyId);

        // 5. Verify audit log
        Assert.Single(audit.Entries);
        var auditEntry = audit.Entries[0];
        Assert.Equal("listing.created-draft", auditEntry.Action);
        Assert.Equal(nameof(Listing), auditEntry.EntityType);
        Assert.Equal(localListing.EtsyListingId.ToString(CultureInfo.InvariantCulture), auditEntry.EntityId);
        Assert.Equal("tophattwaffle", auditEntry.User);
        Assert.Contains("Handmade Speckled Stoneware Mug", auditEntry.Details);
    }

    [Fact]
    public async Task Draft_creation_fails_fast_on_validation_failure_before_calling_Etsy()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), shop.Provider.GetRequiredService<IAuditLog>(),
            NullLogger<ListingCreationService>.Instance);

        var invalidModel = new NewListingModel
        {
            ShopId = FakeEtsyShop.ShopId,
            Title = "Invalid ☕", // Emoji forbidden
            Price = -5.00m,       // Negative price forbidden
            Quantity = 0,         // Zero quantity forbidden
            ShippingProfileId = null, // Missing shipping profile
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateDraftAsync(invalidModel, "tophattwaffle"));
        Assert.Contains("Cannot create listing", ex.Message);
        Assert.Empty(shop.Writes); // Zero requests made to Etsy API
    }
}
