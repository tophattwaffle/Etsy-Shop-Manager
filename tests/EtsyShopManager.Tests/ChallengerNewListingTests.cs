using System.Globalization;
using System.Web;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EtsyShopManager.Tests;

public class ChallengerNewListingTests
{
    private sealed class NoOpAuditLog : IAuditLog
    {
        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null,
            string? userName = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static NewListingModel CreateValidModel() => new()
    {
        ShopId = FakeEtsyShop.ShopId,
        Title = new string('A', 140), // Exactly 140 chars
        Price = 19.99m,
        Quantity = 1,
        TaxonomyId = 100,
        ShippingProfileId = 501,
        ReadinessStateId = 601,
        ReturnPolicyId = 701,
        ShopSectionId = 801,
        Tags = Enumerable.Range(1, 13).Select(i => $"tag{i}").ToList(),
        Materials = Enumerable.Range(1, 13).Select(i => $"material{i}").ToList(),
    };

    [Fact]
    public void Title_boundaries_and_rules_empirical_check()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // Exactly 140 chars -> Valid
        var model140 = CreateValidModel() with { Title = new string('A', 140) };
        Assert.Empty(service.Validate(model140));

        // 141 chars -> Rejected
        var model141 = CreateValidModel() with { Title = new string('A', 141) };
        var p141 = service.Validate(model141);
        Assert.Contains(p141, p => p.Contains("141 characters") && p.Contains("140"));

        // Duplicate % : & + symbols
        foreach (var sym in new[] { '%', ':', '&', '+' })
        {
            var dupModel = CreateValidModel() with { Title = $"Test {sym} Title {sym} Duplicate" };
            var pDup = service.Validate(dupModel);
            Assert.Contains(pDup, p => p.Contains($"\"{sym}\" 2 times"));
        }

        // Single % : & + symbol is allowed
        var singleSymModel = CreateValidModel() with { Title = "100% Cotton: Hand-Made & Soft + Cozy" };
        Assert.Empty(service.Validate(singleSymModel));

        // Forbidden emojis and unicode symbols
        var emojiModel = CreateValidModel() with { Title = "Beautiful Ceramic Mug \u2615" };
        var pEmoji = service.Validate(emojiModel);
        Assert.Contains(pEmoji, p => p.Contains("The title can't contain"));
    }

    [Fact]
    public void Tags_valid_count_and_invalid_characters_are_handled()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // Exactly 13 tags -> Valid
        var model13Tags = CreateValidModel() with { Tags = Enumerable.Range(1, 13).Select(i => $"tag{i}").ToList() };
        Assert.Empty(service.Validate(model13Tags));

        // Exactly 20 chars per tag -> Valid
        var modelTag20 = CreateValidModel() with { Tags = [new string('a', 20)] };
        Assert.Empty(service.Validate(modelTag20));

        // Invalid characters in tags
        var modelInvalidTagChar = CreateValidModel() with { Tags = ["tag#bad", "tag@bad", "tag!bad"] };
        var pInvalidChar = service.Validate(modelInvalidTagChar);
        Assert.True(pInvalidChar.Count >= 3);
    }

    [Fact]
    public void Tags_count_over_13_should_be_rejected()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // 14 tags -> Etsy allows at most 13 tags!
        var model14Tags = CreateValidModel() with { Tags = Enumerable.Range(1, 14).Select(i => $"tag{i}").ToList() };
        var p14 = service.Validate(model14Tags);
        Assert.True(p14.Any(p => p.Contains("14 tags") || p.Contains("13")),
            $"Expected rejection for 14 tags, but Validate returned: [{string.Join("; ", p14)}]");
    }

    [Fact]
    public void Tag_length_over_20_should_be_rejected()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // Tag length 21 characters -> Etsy allows at most 20 chars per tag!
        var modelTag21 = CreateValidModel() with { Tags = [new string('a', 21)] };
        var pTag21 = service.Validate(modelTag21);
        Assert.True(pTag21.Any(p => p.Contains("longer than 20") || p.Contains("20 characters") || p.Contains("20")),
            $"Expected rejection for tag with >20 chars, but Validate returned: [{string.Join("; ", pTag21)}]");
    }

    [Fact]
    public void Materials_valid_count_and_invalid_characters_are_handled()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // Exactly 13 materials -> Valid
        var model13Mat = CreateValidModel() with { Materials = Enumerable.Range(1, 13).Select(i => $"material{i}").ToList() };
        Assert.Empty(service.Validate(model13Mat));

        // Materials with hyphens or punctuation (Etsy only allows letters, numbers, spaces)
        var modelHyphenMat = CreateValidModel() with { Materials = ["cotton-poly", "glass!"] };
        var pHyphen = service.Validate(modelHyphenMat);
        Assert.Contains(pHyphen, p => p.Contains("cotton-poly") && p.Contains("-"));
        Assert.Contains(pHyphen, p => p.Contains("glass!") && p.Contains("!"));
    }

    [Fact]
    public void Materials_count_over_13_should_be_rejected()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // 14 materials -> Etsy allows at most 13 materials!
        var model14Mat = CreateValidModel() with { Materials = Enumerable.Range(1, 14).Select(i => $"material{i}").ToList() };
        var p14Mat = service.Validate(model14Mat);
        Assert.True(p14Mat.Any(p => p.Contains("14 materials") || p.Contains("13")),
            $"Expected rejection for 14 materials, but Validate returned: [{string.Join("; ", p14Mat)}]");
    }

    [Fact]
    public void Price_and_quantity_rejections_empirical_check()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // Price <= 0
        Assert.Contains(service.Validate(CreateValidModel() with { Price = 0m }), p => p.Contains("Price must be greater than 0"));
        Assert.Contains(service.Validate(CreateValidModel() with { Price = -0.01m }), p => p.Contains("Price must be greater than 0"));

        // Quantity <= 0
        Assert.Contains(service.Validate(CreateValidModel() with { Quantity = 0 }), p => p.Contains("Quantity must be at least 1"));
        Assert.Contains(service.Validate(CreateValidModel() with { Quantity = -5 }), p => p.Contains("Quantity must be at least 1"));
    }

    [Fact]
    public void Missing_required_profiles_and_lookups_empirical_check()
    {
        using var shop = new FakeEtsyShop();
        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        // Missing taxonomy
        Assert.Contains(service.Validate(CreateValidModel() with { TaxonomyId = null }), p => p.Contains("category"));
        Assert.Contains(service.Validate(CreateValidModel() with { TaxonomyId = 0 }), p => p.Contains("category"));

        // Missing shipping profile
        Assert.Contains(service.Validate(CreateValidModel() with { ShippingProfileId = null }), p => p.Contains("shipping profile"));
        Assert.Contains(service.Validate(CreateValidModel() with { ShippingProfileId = 0 }), p => p.Contains("shipping profile"));

        // Missing processing profile (readiness state)
        Assert.Contains(service.Validate(CreateValidModel() with { ReadinessStateId = null }), p => p.Contains("processing profile"));
        Assert.Contains(service.Validate(CreateValidModel() with { ReadinessStateId = 0 }), p => p.Contains("processing profile"));

        // Profile not belonging to shop via EditorLookups
        var lookups = new EditorLookups(
            ShippingProfiles: new Dictionary<long, string> { [999] = "Valid Profile" },
            ProcessingProfiles: new Dictionary<long, string> { [888] = "Valid Readiness" },
            ReturnPolicies: new Dictionary<long, string> { [777] = "Valid Policy" },
            Sections: new Dictionary<long, string> { [666] = "Valid Section" },
            ProductionPartners: new Dictionary<long, string>(),
            Categories: new Dictionary<long, string>(),
            CurrencyCode: "USD");

        var wrongProfilesModel = CreateValidModel() with
        {
            ShippingProfileId = 501, // Not 999
            ReadinessStateId = 601,  // Not 888
            ReturnPolicyId = 701,    // Not 777
            ShopSectionId = 801      // Not 666
        };

        var pLookups = service.Validate(wrongProfilesModel, lookups);
        Assert.Contains(pLookups, p => p.Contains("shipping profile belonging to this shop"));
        Assert.Contains(pLookups, p => p.Contains("processing profile belonging to this shop"));
        Assert.Contains(pLookups, p => p.Contains("return policy doesn't belong to this shop"));
        Assert.Contains(pLookups, p => p.Contains("shop section doesn't belong to this shop"));
    }

    [Fact]
    public async Task Draft_creation_in_database_is_draft_and_never_active_empirical_check()
    {
        using var shop = new FakeEtsyShop();

        await using (var db = shop.Db.CreateDbContext())
        {
            db.ShippingProfiles.Add(new ShippingProfile { ShopId = FakeEtsyShop.ShopId, EtsyShippingProfileId = 501, Title = "Standard" });
            db.ProcessingProfiles.Add(new ProcessingProfile { ShopId = FakeEtsyShop.ShopId, ReadinessStateId = 601, ReadinessState = "ready_to_ship", DisplayLabel = "1-3 days" });
            db.TaxonomyNodes.Add(new TaxonomyNode { Id = 100, Name = "Vases", FullPath = "Home > Decor > Vases" });
            await db.SaveChangesAsync();
        }

        var service = new ListingCreationService(shop.Db, shop.Importer,
            shop.Provider.GetRequiredService<EtsyApiClient>(), new NoOpAuditLog(),
            NullLogger<ListingCreationService>.Instance);

        var model = CreateValidModel() with { Title = "Handmade Ceramic Flower Vase" };
        var localId = await service.CreateDraftAsync(model, "testuser");

        // Assert write to Etsy API
        var postWrite = Assert.Single(shop.Writes);
        Assert.Equal("POST", postWrite.Method);
        Assert.Equal($"/v3/application/shops/{FakeEtsyShop.EtsyShopId}/listings", postWrite.Path);

        // Assert query string / body does NOT set state=active
        Assert.DoesNotContain("state=active", postWrite.Body);
        Assert.DoesNotContain("active", postWrite.Body);

        // Assert database record
        await using var verifyDb = shop.Db.CreateDbContext();
        var listing = await verifyDb.Listings.FirstOrDefaultAsync(l => l.Id == localId);
        Assert.NotNull(listing);
        Assert.Equal("draft", listing.State);
        Assert.NotEqual("active", listing.State);
    }
}
