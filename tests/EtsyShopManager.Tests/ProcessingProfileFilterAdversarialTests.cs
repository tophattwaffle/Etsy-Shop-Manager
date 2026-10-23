using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class ProcessingProfileFilterAdversarialTests
{
    private sealed class DummyAuditLog : IAuditLog
    {
        public Task WriteAsync(string action, string? entityType = null, string? entityId = null, string? details = null, string? userName = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed record PlainTemplateListingItem(
        int ListingId,
        string Title,
        int ShopId,
        string ShopName,
        long EtsyListingId,
        string State,
        bool IsMissingOnEtsy,
        IReadOnlyList<string> Skus,
        IReadOnlyList<string> Tags) : ITemplateListingItem;

    private static JsonObject CreateListingWithProducts(
        long etsyListingId,
        string title,
        int shopId,
        long? topLevelReadinessStateId,
        params (long productId, bool isProductDeleted, (long offeringId, long? readinessStateId, bool isOfferingDeleted)[] offerings)[] products)
    {
        var listing = FakeEtsyShop.Listing(etsyListingId, title, withImages: false, withInventory: false);
        listing["shop_id"] = shopId;
        listing["readiness_state_id"] = (JsonNode?)topLevelReadinessStateId;

        var productNodes = new JsonArray();
        foreach (var (productId, isProductDeleted, offerings) in products)
        {
            var offeringNodes = new JsonArray();
            foreach (var (offeringId, readyId, isOfferingDeleted) in offerings)
            {
                var offeringObj = new JsonObject
                {
                    ["offering_id"] = offeringId,
                    ["quantity"] = 5,
                    ["is_enabled"] = true,
                    ["is_deleted"] = isOfferingDeleted,
                    ["price"] = FakeEtsyShop.Money(2500),
                };
                if (readyId.HasValue)
                {
                    offeringObj["readiness_state_id"] = readyId.Value;
                }
                offeringNodes.Add(offeringObj);
            }

            productNodes.Add(new JsonObject
            {
                ["product_id"] = productId,
                ["is_deleted"] = isProductDeleted,
                ["offerings"] = offeringNodes,
            });
        }

        listing["inventory"] = new JsonObject
        {
            ["products"] = productNodes,
        };

        return listing;
    }

    [Fact]
    public async Task Candidate_detection_stress_test_deleted_products_and_offerings_permutations()
    {
        using var shop = new FakeEtsyShop();

        shop.ProcessingProfiles.Clear();
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 601L,
            ["readiness_state"] = "ready_to_ship",
            ["min_processing_days"] = 1,
            ["max_processing_days"] = 3,
            ["processing_days_display_label"] = "Standard 1-3 days",
        });
        shop.ProcessingProfiles.Add(new JsonObject
        {
            ["readiness_state_id"] = 602L,
            ["readiness_state"] = "made_to_order",
            ["min_processing_days"] = 3,
            ["max_processing_days"] = 5,
            ["processing_days_display_label"] = "Rush 3-5 days",
        });

        // 1. Deleted product has profile 601, active product has offering with NO profile, listing has NO top-level profile
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 501,
            title: "Deleted Product Profile vs Active Null Offering",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (5001, isProductDeleted: true, [(50001, 601L, false)]),
            (5002, isProductDeleted: false, [(50002, null, false)])
        ));

        // 2. Deleted product has profile 601, active product has offering with NO profile, but listing HAS top-level profile 602
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 502,
            title: "Deleted Product Profile vs Active Offering with Top-Level Fallback",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: 602L,
            (5003, isProductDeleted: true, [(50003, 601L, false)]),
            (5004, isProductDeleted: false, [(50004, null, false)])
        ));

        // 3. Same product has deleted offering with 602, active offering with NO profile, top-level is null
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 503,
            title: "Deleted Offering Profile vs Active Null Offering",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (5005, isProductDeleted: false, [(50005, 602L, isOfferingDeleted: true), (50006, null, isOfferingDeleted: false)])
        ));

        // 4. Deleted offering 602 vs active offering 601 -> uniform 601, NOT varies
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 504,
            title: "Deleted Offering 602 vs Active Offering 601",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (5006, isProductDeleted: false, [(50007, 602L, isOfferingDeleted: true), (50008, 601L, isOfferingDeleted: false)])
        ));

        // 5. Multiple active products each with same profile 601 -> uniform 601, NOT varies
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 505,
            title: "Multiple Active Products with Same Profile 601",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (5007, isProductDeleted: false, [(50009, 601L, false)]),
            (5008, isProductDeleted: false, [(50010, 601L, false)])
        ));

        // 6. Active product 1 has 601, active product 2 has 602 -> Varies by variation
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 506,
            title: "Active Products with Different Profiles",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (5009, isProductDeleted: false, [(50011, 601L, false)]),
            (5010, isProductDeleted: false, [(50012, 602L, false)])
        ));

        // 7. Empty inventory products array, top-level profile 601
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 507,
            title: "Empty Products Top-Level 601",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: 601L
        ));

        // 8. Empty inventory products array, top-level null
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 508,
            title: "Empty Products Top-Level Null",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null
        ));

        // 9. Active offering with unknown profile ID 9999 (not in shop lookup)
        shop.Listings.Add(CreateListingWithProducts(
            etsyListingId: 509,
            title: "Unknown Profile ID 9999",
            shopId: FakeEtsyShop.ShopId,
            topLevelReadinessStateId: null,
            (5011, isProductDeleted: false, [(50013, 9999L, false)])
        ));

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            new DummyAuditLog());

        var candidates = await service.GetCandidatesAsync(FakeEtsyShop.ShopId, CancellationToken.None);

        // 1. Deleted product profile ignored -> No profile
        var c1 = Assert.Single(candidates, c => c.EtsyListingId == 501);
        Assert.Null(c1.CurrentReadinessStateId);
        Assert.Equal("No profile", c1.CurrentProfileName);
        Assert.False(c1.VariesByVariation);

        // 2. Deleted product profile ignored -> Falls back to top-level 602
        var c2 = Assert.Single(candidates, c => c.EtsyListingId == 502);
        Assert.Equal(602L, c2.CurrentReadinessStateId);
        Assert.Equal("Rush 3-5 days", c2.CurrentProfileName);
        Assert.False(c2.VariesByVariation);

        // 3. Deleted offering profile ignored -> No profile
        var c3 = Assert.Single(candidates, c => c.EtsyListingId == 503);
        Assert.Null(c3.CurrentReadinessStateId);
        Assert.Equal("No profile", c3.CurrentProfileName);
        Assert.False(c3.VariesByVariation);

        // 4. Deleted offering 602 ignored -> Active 601 is uniform
        var c4 = Assert.Single(candidates, c => c.EtsyListingId == 504);
        Assert.Equal(601L, c4.CurrentReadinessStateId);
        Assert.Equal("Standard 1-3 days", c4.CurrentProfileName);
        Assert.False(c4.VariesByVariation);

        // 5. Multiple active products same 601 -> uniform 601
        var c5 = Assert.Single(candidates, c => c.EtsyListingId == 505);
        Assert.Equal(601L, c5.CurrentReadinessStateId);
        Assert.Equal("Standard 1-3 days", c5.CurrentProfileName);
        Assert.False(c5.VariesByVariation);

        // 6. Distinct active profiles -> Varies by variation
        var c6 = Assert.Single(candidates, c => c.EtsyListingId == 506);
        Assert.Null(c6.CurrentReadinessStateId);
        Assert.Equal("Varies by variation", c6.CurrentProfileName);
        Assert.True(c6.VariesByVariation);

        // 7. Empty products fallback to top-level 601
        var c7 = Assert.Single(candidates, c => c.EtsyListingId == 507);
        Assert.Equal(601L, c7.CurrentReadinessStateId);
        Assert.Equal("Standard 1-3 days", c7.CurrentProfileName);
        Assert.False(c7.VariesByVariation);

        // 8. Empty products fallback to null -> No profile
        var c8 = Assert.Single(candidates, c => c.EtsyListingId == 508);
        Assert.Null(c8.CurrentReadinessStateId);
        Assert.Equal("No profile", c8.CurrentProfileName);
        Assert.False(c8.VariesByVariation);

        // 9. Unknown profile ID 9999 -> fallback display name "#9999"
        var c9 = Assert.Single(candidates, c => c.EtsyListingId == 509);
        Assert.Equal(9999L, c9.CurrentReadinessStateId);
        Assert.Equal("#9999", c9.CurrentProfileName);
        Assert.False(c9.VariesByVariation);
    }

    [Fact]
    public void TemplateFilterMatcher_whitespace_and_casing_adversarial_tests()
    {
        var standardCandidate = new ProcessingProfileCandidate(
            ListingId: 1,
            Title: "Standard Item",
            ShopId: 1,
            ShopName: "Shop A",
            EtsyListingId: 101,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["SKU-1"],
            Tags: ["tag1"],
            CurrentReadinessStateId: 601,
            CurrentProfileName: "Standard 1-3 Days",
            VariesByVariation: false);

        var variesCandidate = new ProcessingProfileCandidate(
            ListingId: 2,
            Title: "Varying Item",
            ShopId: 1,
            ShopName: "Shop A",
            EtsyListingId: 102,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["SKU-2"],
            Tags: ["tag2"],
            CurrentReadinessStateId: null,
            CurrentProfileName: "Varies by variation",
            VariesByVariation: true);

        var noneCandidate = new ProcessingProfileCandidate(
            ListingId: 3,
            Title: "No Profile Item",
            ShopId: 1,
            ShopName: "Shop A",
            EtsyListingId: 103,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["SKU-3"],
            Tags: ["tag3"],
            CurrentReadinessStateId: null,
            CurrentProfileName: "No profile",
            VariesByVariation: false);

        // Whitespace-padded "all", empty, whitespace-only, null
        string?[] allVariants = [null, "", "   ", "all", "ALL", "  all  ", "  ALL  ", "All"];
        foreach (var v in allVariants)
        {
            Assert.True(TemplateFilterMatcher.Matches(standardCandidate, new TemplateListingFilter { ProcessingProfile = v }));
            Assert.True(TemplateFilterMatcher.Matches(variesCandidate, new TemplateListingFilter { ProcessingProfile = v }));
            Assert.True(TemplateFilterMatcher.Matches(noneCandidate, new TemplateListingFilter { ProcessingProfile = v }));
        }

        // Whitespace-padded and mixed-case "varies"
        string[] variesVariants = ["varies", "VARIES", "  varies  ", "  VaRiEs  ", "Varies by variation", "  VARIES BY VARIATION  ", "varies by variation"];
        foreach (var v in variesVariants)
        {
            Assert.True(TemplateFilterMatcher.Matches(variesCandidate, new TemplateListingFilter { ProcessingProfile = v }));
            Assert.False(TemplateFilterMatcher.Matches(standardCandidate, new TemplateListingFilter { ProcessingProfile = v }));
            Assert.False(TemplateFilterMatcher.Matches(noneCandidate, new TemplateListingFilter { ProcessingProfile = v }));
        }

        // Whitespace-padded and mixed-case "none"
        string[] noneVariants = ["none", "NONE", "  none  ", "  No profile  ", "NO PROFILE", "no_profile", "  NO_PROFILE  ", "—", "  —  "];
        foreach (var v in noneVariants)
        {
            Assert.True(TemplateFilterMatcher.Matches(noneCandidate, new TemplateListingFilter { ProcessingProfile = v }));
            Assert.False(TemplateFilterMatcher.Matches(standardCandidate, new TemplateListingFilter { ProcessingProfile = v }));
            Assert.False(TemplateFilterMatcher.Matches(variesCandidate, new TemplateListingFilter { ProcessingProfile = v }));
        }

        // Whitespace-padded numeric ID
        Assert.True(TemplateFilterMatcher.Matches(standardCandidate, new TemplateListingFilter { ProcessingProfile = "  601  " }));
        Assert.False(TemplateFilterMatcher.Matches(variesCandidate, new TemplateListingFilter { ProcessingProfile = "  601  " }));
        Assert.False(TemplateFilterMatcher.Matches(noneCandidate, new TemplateListingFilter { ProcessingProfile = "  601  " }));

        // Mixed-case and whitespace-padded display name
        Assert.True(TemplateFilterMatcher.Matches(standardCandidate, new TemplateListingFilter { ProcessingProfile = "  standard 1-3 days  " }));
        Assert.True(TemplateFilterMatcher.Matches(standardCandidate, new TemplateListingFilter { ProcessingProfile = "STANDARD 1-3 DAYS" }));
        Assert.False(TemplateFilterMatcher.Matches(variesCandidate, new TemplateListingFilter { ProcessingProfile = "standard 1-3 days" }));
        Assert.False(TemplateFilterMatcher.Matches(noneCandidate, new TemplateListingFilter { ProcessingProfile = "standard 1-3 days" }));
    }

    [Fact]
    public void TemplateFilterMatcher_invalid_and_adversarial_strings_handled_cleanly()
    {
        var candidate = new ProcessingProfileCandidate(
            ListingId: 1,
            Title: "Test Item",
            ShopId: 1,
            ShopName: "Shop",
            EtsyListingId: 101,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["SKU"],
            Tags: ["tag"],
            CurrentReadinessStateId: 601,
            CurrentProfileName: "Standard",
            VariesByVariation: false);

        string[] adversarialFilters =
        [
            "nonexistent_profile",
            "9999999999999999999999999999999999999999999999999999999", // overflow for long.TryParse
            "-1",
            "-999999999",
            "0",
            "null",
            "undefined",
            "<script>alert(1)</script>",
            "'; DROP TABLE Profiles; --",
            "   invalid padded string   ",
            "!@#$%^&*()_+"
        ];

        foreach (var badFilter in adversarialFilters)
        {
            // Must not throw exceptions and must safely evaluate to false
            var matched = TemplateFilterMatcher.Matches(candidate, new TemplateListingFilter { ProcessingProfile = badFilter });
            Assert.False(matched, $"Filter '{badFilter}' should not match candidate.");
        }
    }

    [Fact]
    public void TemplateFilterMatcher_numeric_display_label_collision_handling()
    {
        // Candidate where display label IS a numeric string ("2024"), but ReadinessStateId is 777
        var numericNameCandidate = new ProcessingProfileCandidate(
            ListingId: 1,
            Title: "Numbered Profile Item",
            ShopId: 1,
            ShopName: "Shop",
            EtsyListingId: 101,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: [],
            Tags: [],
            CurrentReadinessStateId: 777,
            CurrentProfileName: "2024",
            VariesByVariation: false);

        // Filtering by ReadinessStateId "777" must match
        Assert.True(TemplateFilterMatcher.Matches(numericNameCandidate, new TemplateListingFilter { ProcessingProfile = "777" }));

        // Filtering by display label "2024" (parsed as long 2024) must ALSO match via name fallback
        Assert.True(TemplateFilterMatcher.Matches(numericNameCandidate, new TemplateListingFilter { ProcessingProfile = "2024" }));

        // Filtering by other numeric ID "999" must NOT match
        Assert.False(TemplateFilterMatcher.Matches(numericNameCandidate, new TemplateListingFilter { ProcessingProfile = "999" }));
    }

    [Fact]
    public void TemplateFilterMatcher_non_candidate_item_returns_false_for_profile_filter()
    {
        var nonCandidateItem = new PlainTemplateListingItem(
            ListingId: 10,
            Title: "Generic Listing",
            ShopId: 1,
            ShopName: "Shop",
            EtsyListingId: 200,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: ["GEN-1"],
            Tags: ["tag"]);

        // When profile filter is null or "all", non-candidate item matches normally
        Assert.True(TemplateFilterMatcher.Matches(nonCandidateItem, new TemplateListingFilter { ProcessingProfile = null }));
        Assert.True(TemplateFilterMatcher.Matches(nonCandidateItem, new TemplateListingFilter { ProcessingProfile = "all" }));

        // When specific profile filter is given, non-candidate item returns false
        Assert.False(TemplateFilterMatcher.Matches(nonCandidateItem, new TemplateListingFilter { ProcessingProfile = "601" }));
        Assert.False(TemplateFilterMatcher.Matches(nonCandidateItem, new TemplateListingFilter { ProcessingProfile = "varies" }));
        Assert.False(TemplateFilterMatcher.Matches(nonCandidateItem, new TemplateListingFilter { ProcessingProfile = "none" }));
    }

    [Fact]
    public void TemplateFilterMatcher_full_combinations_matrix()
    {
        var candidates = new List<ProcessingProfileCandidate>
        {
            // [0] Candidate A: Shop 1, Active, Title "Ceramic Blue Mug", SKU "MUG-BLU", Tag "kitchen", Profile 601 ("Standard")
            new(101, "Ceramic Blue Mug", 1, "Shop 1", 10001, "active", false, ["MUG-BLU"], ["kitchen", "mug"], 601, "Standard", false),

            // [1] Candidate B: Shop 1, Active, Title "Ceramic Red Mug", SKU "MUG-RED", Tag "kitchen", Profile 602 ("Express")
            new(102, "Ceramic Red Mug", 1, "Shop 1", 10002, "active", false, ["MUG-RED"], ["kitchen", "mug"], 602, "Express", false),

            // [2] Candidate C: Shop 2, Active, Title "Ceramic Green Mug", SKU "MUG-GRN", Tag "kitchen", Profile 601 ("Standard")
            new(103, "Ceramic Green Mug", 2, "Shop 2", 10003, "active", false, ["MUG-GRN"], ["kitchen", "mug"], 601, "Standard", false),

            // [3] Candidate D: Shop 1, Draft, Title "Ceramic Yellow Plate", SKU "PLT-YEL", Tag "tableware", Profile 601 ("Standard")
            new(104, "Ceramic Yellow Plate", 1, "Shop 1", 10004, "draft", false, ["PLT-YEL"], ["tableware"], 601, "Standard", false),

            // [4] Candidate E: Shop 1, Active, Title "Ceramic Rainbow Planter", SKU "PLN-RNB", Tag "garden", Profile null ("Varies by variation"), Varies=true
            new(105, "Ceramic Rainbow Planter", 1, "Shop 1", 10005, "active", false, ["PLN-RNB"], ["garden"], null, "Varies by variation", true),

            // [5] Candidate F: Shop 1, Active, Title "Raw Clay Block", SKU "CLAY-01", Tag "craft", Profile null ("No profile"), Varies=false
            new(106, "Raw Clay Block", 1, "Shop 1", 10006, "active", false, ["CLAY-01"], ["craft"], null, "No profile", false),

            // [6] Candidate G: Shop 1, Active, Missing on Etsy, Title "Old Deleted Mug", SKU "MUG-OLD", Tag "kitchen", Profile 601 ("Standard")
            new(107, "Old Deleted Mug", 1, "Shop 1", 10007, "active", true, ["MUG-OLD"], ["kitchen"], 601, "Standard", false)
        };

        // Query 1: Text "Ceramic", Shop 1, Status "active", Profile "601"
        // Matches: Candidate A ONLY.
        // Candidate B rejected: Profile is 602
        // Candidate C rejected: Shop is 2
        // Candidate D rejected: Status is draft
        // Candidate E rejected: Profile is varies
        // Candidate F rejected: Text does not contain "Ceramic"
        // Candidate G rejected: Missing on Etsy
        var filter1 = new TemplateListingFilter
        {
            Text = "Ceramic",
            ShopId = 1,
            Status = "active",
            ProcessingProfile = "601",
        };
        var res1 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter1)).ToList();
        Assert.Single(res1);
        Assert.Equal(101, res1[0].ListingId);

        // Query 2: Text "Ceramic", Shop 1, Status "active", Profile "varies"
        // Matches: Candidate E ONLY.
        var filter2 = new TemplateListingFilter
        {
            Text = "Ceramic",
            ShopId = 1,
            Status = "active",
            ProcessingProfile = "varies",
        };
        var res2 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter2)).ToList();
        Assert.Single(res2);
        Assert.Equal(105, res2[0].ListingId);

        // Query 3: Text "Ceramic", Shop 1, Status "all", Profile "Standard"
        // Matches: Candidate A (active) and Candidate D (draft).
        var filter3 = new TemplateListingFilter
        {
            Text = "Ceramic",
            ShopId = 1,
            Status = "all",
            ProcessingProfile = "Standard",
        };
        var res3 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter3)).ToList();
        Assert.Equal(2, res3.Count);
        Assert.Contains(res3, c => c.ListingId == 101);
        Assert.Contains(res3, c => c.ListingId == 104);

        // Query 4: Text by Etsy ID "10004", Shop 1, Status "draft", Profile "601"
        // Matches: Candidate D ONLY.
        var filter4 = new TemplateListingFilter
        {
            Text = "10004",
            ShopId = 1,
            Status = "draft",
            ProcessingProfile = "601",
        };
        var res4 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter4)).ToList();
        Assert.Single(res4);
        Assert.Equal(104, res4[0].ListingId);

        // Query 5: Text by SKU "MUG-GRN", Shop 2, Status "active", Profile "601"
        // Matches: Candidate C ONLY.
        var filter5 = new TemplateListingFilter
        {
            Text = "MUG-GRN",
            ShopId = 2,
            Status = "active",
            ProcessingProfile = "601",
        };
        var res5 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter5)).ToList();
        Assert.Single(res5);
        Assert.Equal(103, res5[0].ListingId);

        // Query 6: Text by Tag "craft", Shop 1, Status "active", Profile "none"
        // Matches: Candidate F ONLY.
        var filter6 = new TemplateListingFilter
        {
            Text = "craft",
            ShopId = 1,
            Status = "active",
            ProcessingProfile = "none",
        };
        var res6 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter6)).ToList();
        Assert.Single(res6);
        Assert.Equal(106, res6[0].ListingId);

        // Query 7: Missing listings query: Status "missing", Profile "Standard"
        // Matches: Candidate G ONLY.
        var filter7 = new TemplateListingFilter
        {
            Status = "missing",
            ProcessingProfile = "Standard",
        };
        var res7 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter7)).ToList();
        Assert.Single(res7);
        Assert.Equal(107, res7[0].ListingId);

        // Query 8: Contradictory criteria: Text "Ceramic", Shop 2, Status "draft", Profile "Express"
        // Matches: NONE.
        var filter8 = new TemplateListingFilter
        {
            Text = "Ceramic",
            ShopId = 2,
            Status = "draft",
            ProcessingProfile = "Express",
        };
        var res8 = candidates.Where(c => TemplateFilterMatcher.Matches(c, filter8)).ToList();
        Assert.Empty(res8);
    }
}
