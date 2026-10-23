using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ProcessingProfileFilterTests
{
    private sealed class TestAuditLog : IAuditLog
    {
        public List<(string Action, string? EntityType, string? EntityId, string? Details, string? User)> Entries { get; } = [];
        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null, string? userName = null, CancellationToken ct = default)
        {
            Entries.Add((action, entityType, entityId, details, userName));
            return Task.CompletedTask;
        }
    }

    private static JsonObject CreateListingWithOfferings(
        long etsyListingId,
        string title,
        int shopId,
        long? topLevelReadinessStateId,
        params (long offeringId, long? readinessStateId, bool isDeleted)[] offerings)
    {
        var listing = FakeEtsyShop.Listing(etsyListingId, title, withImages: false, withInventory: false);
        listing["shop_id"] = shopId;
        listing["readiness_state_id"] = (JsonNode?)topLevelReadinessStateId;

        var offeringNodes = new JsonArray();
        foreach (var (offeringId, readyId, isDeleted) in offerings)
        {
            var offeringObj = new JsonObject
            {
                ["offering_id"] = offeringId,
                ["quantity"] = 5,
                ["is_enabled"] = true,
                ["is_deleted"] = isDeleted,
                ["price"] = FakeEtsyShop.Money(2000),
            };
            if (readyId.HasValue)
            {
                offeringObj["readiness_state_id"] = readyId.Value;
            }
            offeringNodes.Add(offeringObj);
        }

        listing["inventory"] = new JsonObject
        {
            ["products"] = new JsonArray
            {
                new JsonObject
                {
                    ["product_id"] = etsyListingId * 100 + 1,
                    ["is_deleted"] = false,
                    ["offerings"] = offeringNodes,
                }
            }
        };

        return listing;
    }

    [Fact]
    public async Task Candidate_detection_detects_single_profile_mixed_variations_and_no_profile()
    {
        using var shop = new FakeEtsyShop();

        // Setup 2 processing profiles in shop
        shop.ProcessingProfiles.Clear();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 601L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 3,
            ["processing_days_display_label"] = "1-3 business days",
        });
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "made_to_order",
            ["min_processing_days"] = 3,
            ["max_processing_days"] = 5,
            ["processing_days_display_label"] = "3-5 business days",
        });

        // 1. Uniform profile: offerings all have 601
        shop.Listings.Add(CreateListingWithOfferings(
            etsyListingId: 101,
            title: "Uniform Profile Listing",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: 601L,
            (1001, 601L, false),
            (1002, 601L, false)
        ));

        // 2. Mixed variation profiles: offering 1 has 601, offering 2 has 602
        shop.Listings.Add(CreateListingWithOfferings(
            etsyListingId: 102,
            title: "Mixed Profiles Listing",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (2001, 601L, false),
            (2002, 602L, false)
        ));

        // 3. No profile anywhere
        shop.Listings.Add(CreateListingWithOfferings(
            etsyListingId: 103,
            title: "No Profile Listing",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (3001, null, false),
            (3002, null, false)
        ));

        // 4. Fallback to listing.ReadinessStateId when offerings have no profile
        shop.Listings.Add(CreateListingWithOfferings(
            etsyListingId: 104,
            title: "Top-Level Fallback Listing",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: 602L,
            (4001, null, false),
            (4002, null, false)
        ));

        // 5. Deleted offerings ignored: offering 1 active has 601, offering 2 deleted has 602
        shop.Listings.Add(CreateListingWithOfferings(
            etsyListingId: 105,
            title: "Deleted Offering Ignored Listing",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (5001, 601L, false),
            (5002, 602L, true) // deleted offering
        ));

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new TestAuditLog());

        var candidates = await service.GetCandidatesAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        // 1. Uniform
        var c1 = Assert.Single(candidates, c => c.EtsyListingId == 101);
        Assert.Equal(601L, c1.CurrentReadinessStateId);
        Assert.Equal("1-3 business days", c1.CurrentProfileName);
        Assert.False(c1.VariesByVariation);

        // 2. Mixed
        var c2 = Assert.Single(candidates, c => c.EtsyListingId == 102);
        Assert.Null(c2.CurrentReadinessStateId);
        Assert.Equal("Varies by variation", c2.CurrentProfileName);
        Assert.True(c2.VariesByVariation);

        // 3. No profile
        var c3 = Assert.Single(candidates, c => c.EtsyListingId == 103);
        Assert.Null(c3.CurrentReadinessStateId);
        Assert.Equal("No profile", c3.CurrentProfileName);
        Assert.False(c3.VariesByVariation);

        // 4. Fallback
        var c4 = Assert.Single(candidates, c => c.EtsyListingId == 104);
        Assert.Equal(602L, c4.CurrentReadinessStateId);
        Assert.Equal("3-5 business days", c4.CurrentProfileName);
        Assert.False(c4.VariesByVariation);

        // 5. Deleted offering ignored -> uniform 601
        var c5 = Assert.Single(candidates, c => c.EtsyListingId == 105);
        Assert.Equal(601L, c5.CurrentReadinessStateId);
        Assert.Equal("1-3 business days", c5.CurrentProfileName);
        Assert.False(c5.VariesByVariation);
    }

    [Fact]
    public void TemplateFilterMatcher_matches_all_profile_filter_variations()
    {
        var single601 = new ProcessingProfileCandidate(
            ListingId: 1,
            Title: "Ceramic Mug",
            ShopId: 1,
            ShopName: "ClayWorks",
            EtsyListingId: 1001,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["MUG-01"],
            Tags: ["mug", "kitchen"],
            CurrentReadinessStateId: 601,
            CurrentProfileName: "1-3 days",
            VariesByVariation: false);

        var single602 = new ProcessingProfileCandidate(
            ListingId: 2,
            Title: "Custom Mug",
            ShopId: 1,
            ShopName: "ClayWorks",
            EtsyListingId: 1002,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["MUG-02"],
            Tags: ["custom", "mug"],
            CurrentReadinessStateId: 602,
            CurrentProfileName: "3-5 days",
            VariesByVariation: false);

        var varies = new ProcessingProfileCandidate(
            ListingId: 3,
            Title: "Variegated Planter",
            ShopId: 1,
            ShopName: "ClayWorks",
            EtsyListingId: 1003,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["PLANT-VAR"],
            Tags: ["planter"],
            CurrentReadinessStateId: null,
            CurrentProfileName: "Varies by variation",
            VariesByVariation: true);

        var noProfile = new ProcessingProfileCandidate(
            ListingId: 4,
            Title: "Raw Clay Block",
            ShopId: 1,
            ShopName: "ClayWorks",
            EtsyListingId: 1004,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["RAW-01"],
            Tags: ["clay"],
            CurrentReadinessStateId: null,
            CurrentProfileName: "No profile",
            VariesByVariation: false);

        // 1. Null / empty / "all" passes everything
        Assert.True(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = null }));
        Assert.True(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "" }));
        Assert.True(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "all" }));
        Assert.True(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "ALL" }));
        Assert.True(TemplateFilterMatcher.Matches(varies, new TemplateListingFilter { ProcessingProfile = "all" }));
        Assert.True(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "all" }));

        // 2. "varies" or "Varies by variation"
        Assert.False(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "varies" }));
        Assert.False(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "varies" }));
        Assert.True(TemplateFilterMatcher.Matches(varies, new TemplateListingFilter { ProcessingProfile = "varies" }));
        Assert.True(TemplateFilterMatcher.Matches(varies, new TemplateListingFilter { ProcessingProfile = "VARIES" }));
        Assert.True(TemplateFilterMatcher.Matches(varies, new TemplateListingFilter { ProcessingProfile = "Varies by variation" }));

        // 3. "none", "No profile", "no_profile", "—"
        Assert.False(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "none" }));
        Assert.False(TemplateFilterMatcher.Matches(varies, new TemplateListingFilter { ProcessingProfile = "none" }));
        Assert.True(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "none" }));
        Assert.True(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "NONE" }));
        Assert.True(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "No profile" }));
        Assert.True(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "no_profile" }));
        Assert.True(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "—" }));

        // 4. Specific profile by ID string
        Assert.True(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "601" }));
        Assert.False(TemplateFilterMatcher.Matches(single602, new TemplateListingFilter { ProcessingProfile = "601" }));
        Assert.False(TemplateFilterMatcher.Matches(varies, new TemplateListingFilter { ProcessingProfile = "601" }));
        Assert.False(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "601" }));

        // 5. Specific profile by Name
        Assert.True(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "1-3 days" }));
        Assert.True(TemplateFilterMatcher.Matches(single601, new TemplateListingFilter { ProcessingProfile = "1-3 DAYS" }));
        Assert.False(TemplateFilterMatcher.Matches(single602, new TemplateListingFilter { ProcessingProfile = "1-3 days" }));
        Assert.False(TemplateFilterMatcher.Matches(varies, new TemplateListingFilter { ProcessingProfile = "1-3 days" }));
        Assert.False(TemplateFilterMatcher.Matches(noProfile, new TemplateListingFilter { ProcessingProfile = "1-3 days" }));
    }

    [Fact]
    public void TemplateFilterMatcher_combines_ProcessingProfile_with_search_shop_and_status()
    {
        var target = new ProcessingProfileCandidate(
            ListingId: 10,
            Title: "Handmade Ceramic Planter",
            ShopId: 1,
            ShopName: "ClayWorks",
            EtsyListingId: 9901,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["PLANTER-SM"],
            Tags: ["gardening", "pot"],
            CurrentReadinessStateId: 601,
            CurrentProfileName: "1-3 days",
            VariesByVariation: false);

        var differentProfile = target with { ListingId = 11, CurrentReadinessStateId = 602, CurrentProfileName = "3-5 days" };
        var differentShop = target with { ListingId = 12, ShopId = 2, ShopName = "WoodWorks" };
        var differentStatus = target with { ListingId = 13, State = "draft" };
        var differentText = target with { ListingId = 14, Title = "Wooden Spoon", Skus = ["SPOON-01"], Tags = ["kitchen"] };

        var filter = new TemplateListingFilter
        {
            Text = "ceramic",
            ShopId = 1,
            Status = "active",
            ProcessingProfile = "601",
        };

        Assert.True(TemplateFilterMatcher.Matches(target, filter));
        Assert.False(TemplateFilterMatcher.Matches(differentProfile, filter)); // profile mismatch
        Assert.False(TemplateFilterMatcher.Matches(differentShop, filter)); // shop mismatch
        Assert.False(TemplateFilterMatcher.Matches(differentStatus, filter)); // status mismatch
        Assert.False(TemplateFilterMatcher.Matches(differentText, filter)); // search text mismatch
    }

    [Fact]
    public void Candidate_table_visible_count_and_bulk_apply_respect_active_profile_filter()
    {
        var candidates = new List<ProcessingProfileCandidate>
        {
            new(1, "Item 1", 1, "Shop 1", 101, "active", false, [], [], 601, "1-3 days", false),
            new(2, "Item 2", 1, "Shop 1", 102, "active", false, [], [], 602, "3-5 days", false),
            new(3, "Item 3", 1, "Shop 1", 103, "active", false, [], [], null, "Varies by variation", true),
            new(4, "Item 4", 1, "Shop 1", 104, "active", false, [], [], null, "No profile", false),
        };

        // Filter: only profile 601
        var filter601 = new TemplateListingFilter { ProcessingProfile = "601" };
        var visible601 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter601)).ToList();
        Assert.Single(visible601);
        Assert.Equal(1, visible601[0].ListingId);

        // Filter: only "varies"
        var filterVaries = new TemplateListingFilter { ProcessingProfile = "varies" };
        var visibleVaries = candidates.Where(c => TemplateFilterMatcher.Matches(c, filterVaries)).ToList();
        Assert.Single(visibleVaries);
        Assert.Equal(3, visibleVaries[0].ListingId);

        // Filter: only "none"
        var filterNone = new TemplateListingFilter { ProcessingProfile = "none" };
        var visibleNone = candidates.Where(c => TemplateFilterMatcher.Matches(c, filterNone)).ToList();
        Assert.Single(visibleNone);
        Assert.Equal(4, visibleNone[0].ListingId);

        // Selection across filter transitions:
        // Suppose items 1, 2, 3, 4 were picked
        var picked = new HashSet<int> { 1, 2, 3, 4 };

        // When filter is set to "601", target listings for preview MUST intersect with visible candidates
        var targetIds601 = picked.Intersect(visible601.Select(c => c.ListingId)).ToList();
        Assert.Equal([1], targetIds601);

        // When filter is set to "none", target listings for preview MUST intersect with visible candidates
        var targetIdsNone = picked.Intersect(visibleNone.Select(c => c.ListingId)).ToList();
        Assert.Equal([4], targetIdsNone);
    }
}
