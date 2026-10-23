using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class TemplateFilterTests
{
    private sealed record TestItem : ITemplateListingItem
    {
        public int ListingId { get; init; } = 1;
        public string Title { get; init; } = "Test Listing";
        public int ShopId { get; init; } = 1;
        public string ShopName { get; init; } = "Shop One";
        public long EtsyListingId { get; init; } = 123456789;
        public string State { get; init; } = "active";
        public bool IsMissingOnEtsy { get; init; }
        public IReadOnlyList<string> Skus { get; init; } = [];
        public IReadOnlyList<string> Tags { get; init; } = [];
    }

    [Fact]
    public void Matches_EmptyFilter_MatchesNonMissingItem()
    {
        var item = new TestItem();
        var filter = new TemplateListingFilter();

        Assert.True(TemplateFilterMatcher.Matches(item, filter));
    }

    [Fact]
    public void Matches_TextFilter_MatchesTitle_CaseInsensitiveAndSubstring()
    {
        var item = new TestItem { Title = "Handmade Ceramic Mug" };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "ceramic" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "MUG" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "  Handmade  " }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "Plate" }));
    }

    [Fact]
    public void Matches_TextFilter_MatchesSku()
    {
        var item = new TestItem
        {
            Title = "Vase",
            Skus = ["VASE-BLUE-01", "VASE-RED-02"]
        };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "blue" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "VASE-RED" }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "GREEN" }));
    }

    [Fact]
    public void Matches_TextFilter_MatchesTag()
    {
        var item = new TestItem
        {
            Title = "Vase",
            Tags = ["pottery", "home decor", "gift"]
        };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "POTTERY" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "decor" }));
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "kitchen" }));
    }

    [Fact]
    public void Matches_TextFilter_MatchesNumericEtsyListingId()
    {
        var item = new TestItem { EtsyListingId = 9876543210 };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "9876543210" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "  9876543210  " }));
        // Substring numeric matches are not Etsy ID match (unless contained in title/sku/tag)
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "9876" }));
    }

    [Fact]
    public void Matches_TextFilter_WhitespaceOrNull_MatchesAll()
    {
        var item = new TestItem { Title = "Vase" };

        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = null }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "" }));
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter { Text = "   " }));
    }

    [Fact]
    public void Matches_ShopFilter_MatchesSpecificShopOnly()
    {
        var itemShop1 = new TestItem { ShopId = 1 };
        var itemShop2 = new TestItem { ShopId = 2 };

        var filterAll = new TemplateListingFilter { ShopId = null };
        var filterShop1 = new TemplateListingFilter { ShopId = 1 };
        var filterShop2 = new TemplateListingFilter { ShopId = 2 };

        Assert.True(TemplateFilterMatcher.Matches(itemShop1, filterAll));
        Assert.True(TemplateFilterMatcher.Matches(itemShop2, filterAll));

        Assert.True(TemplateFilterMatcher.Matches(itemShop1, filterShop1));
        Assert.False(TemplateFilterMatcher.Matches(itemShop2, filterShop1));

        Assert.False(TemplateFilterMatcher.Matches(itemShop1, filterShop2));
        Assert.True(TemplateFilterMatcher.Matches(itemShop2, filterShop2));
    }

    [Theory]
    [InlineData("all", "active", false, true)]
    [InlineData("all", "draft", false, true)]
    [InlineData("all", "active", true, false)]
    [InlineData("active", "active", false, true)]
    [InlineData("active", "draft", false, false)]
    [InlineData("active", "active", true, false)]
    [InlineData("draft", "draft", false, true)]
    [InlineData("draft", "active", false, false)]
    [InlineData("expired", "expired", false, true)]
    [InlineData("expired", "active", false, false)]
    [InlineData("sold_out", "sold_out", false, true)]
    [InlineData("sold_out", "active", false, false)]
    [InlineData("inactive", "inactive", false, true)]
    [InlineData("inactive", "active", false, false)]
    [InlineData("missing", "active", true, true)]
    [InlineData("missing", "draft", true, true)]
    [InlineData("missing", "active", false, false)]
    public void Matches_StatusFilter_CorrectlyFiltersStateAndMissing(string filterStatus, string itemState, bool isMissing, bool expected)
    {
        var item = new TestItem { State = itemState, IsMissingOnEtsy = isMissing };
        var filter = new TemplateListingFilter { Status = filterStatus };

        Assert.Equal(expected, TemplateFilterMatcher.Matches(item, filter));
    }

    [Fact]
    public void Matches_CombinedFilters_RequiresAllCriteria()
    {
        var item = new TestItem
        {
            Title = "Silk Scarf",
            ShopId = 10,
            State = "active",
            IsMissingOnEtsy = false,
            Tags = ["fashion", "accessory"]
        };

        // All match
        Assert.True(TemplateFilterMatcher.Matches(item, new TemplateListingFilter
        {
            Text = "silk",
            ShopId = 10,
            Status = "active"
        }));

        // Text mismatch
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter
        {
            Text = "wool",
            ShopId = 10,
            Status = "active"
        }));

        // Shop mismatch
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter
        {
            Text = "silk",
            ShopId = 20,
            Status = "active"
        }));

        // Status mismatch
        Assert.False(TemplateFilterMatcher.Matches(item, new TemplateListingFilter
        {
            Text = "silk",
            ShopId = 10,
            Status = "draft"
        }));
    }

    [Fact]
    public void ScopedBulkSelection_TriStateAndToggleLogic()
    {
        var item1 = new TestItem { ListingId = 1, Title = "Blue Mug", ShopId = 1 };
        var item2 = new TestItem { ListingId = 2, Title = "Red Mug", ShopId = 1 };
        var item3 = new TestItem { ListingId = 3, Title = "Green Bowl", ShopId = 2 };
        var allItems = new List<TestItem> { item1, item2, item3 };

        var picked = new HashSet<int>();

        // Local helper representing Blazor page state
        bool? GetCandidateHeaderChecked(IReadOnlyList<TestItem> visible)
        {
            var visibleIds = visible.Select(c => c.ListingId).ToList();
            if (visibleIds.Count == 0) return false;
            var pickedCount = visibleIds.Count(picked.Contains);
            if (pickedCount == 0) return false;
            if (pickedCount == visibleIds.Count) return true;
            return null;
        }

        void TogglePickAllVisible(IReadOnlyList<TestItem> visible)
        {
            var visibleIds = visible.Select(c => c.ListingId).ToList();
            if (visibleIds.Count == 0) return;
            var allPicked = visibleIds.All(picked.Contains);
            if (allPicked)
                picked.ExceptWith(visibleIds);
            else
                picked.UnionWith(visibleIds);
        }

        // Filter: Shop 1 only (item1 and item2 visible)
        var filterShop1 = new TemplateListingFilter { ShopId = 1 };
        var visibleShop1 = allItems.Where(i => TemplateFilterMatcher.Matches(i, filterShop1)).ToList();
        Assert.Equal(2, visibleShop1.Count);

        // Initially nothing picked: header is false
        Assert.False(GetCandidateHeaderChecked(visibleShop1));

        // Pick item 1 only: header is null (indeterminate)
        picked.Add(1);
        Assert.Null(GetCandidateHeaderChecked(visibleShop1));

        // Click toggle: since not all visible were picked, both visible items (1 & 2) should now be picked
        TogglePickAllVisible(visibleShop1);
        Assert.True(GetCandidateHeaderChecked(visibleShop1));
        Assert.Contains(1, picked);
        Assert.Contains(2, picked);
        Assert.DoesNotContain(3, picked);

        // Now switch filter: Text = "Green" (item3 only visible)
        var filterGreen = new TemplateListingFilter { Text = "Green" };
        var visibleGreen = allItems.Where(i => TemplateFilterMatcher.Matches(i, filterGreen)).ToList();
        Assert.Single(visibleGreen);
        Assert.Equal(3, visibleGreen[0].ListingId);

        // Under Green filter, item 3 is not picked yet: header is false
        Assert.False(GetCandidateHeaderChecked(visibleGreen));

        // Toggle pick for Green: item 3 is picked
        TogglePickAllVisible(visibleGreen);
        Assert.True(GetCandidateHeaderChecked(visibleGreen));
        Assert.Contains(3, picked);
        // Items 1 & 2 from previous filter are preserved!
        Assert.Contains(1, picked);
        Assert.Contains(2, picked);

        // Switch back to Shop 1 filter (items 1 & 2 visible, all picked)
        Assert.True(GetCandidateHeaderChecked(visibleShop1));

        // Toggle pick all on visibleShop1: should unpick ONLY items 1 & 2, leaving item 3 intact!
        TogglePickAllVisible(visibleShop1);
        Assert.False(GetCandidateHeaderChecked(visibleShop1));
        Assert.DoesNotContain(1, picked);
        Assert.DoesNotContain(2, picked);
        Assert.Contains(3, picked); // Item 3 from Shop 2 is still picked!

        // Empty visible test
        var filterNone = new TemplateListingFilter { Text = "NonExistent" };
        var visibleNone = allItems.Where(i => TemplateFilterMatcher.Matches(i, filterNone)).ToList();
        Assert.Empty(visibleNone);
        Assert.False(GetCandidateHeaderChecked(visibleNone));
    }

    [Fact]
    public async Task TextBlockService_PopulatesFilterFields_ForUsesAndCandidates()
    {
        var shop = new FakeEtsyShop();
        var listing1 = FakeEtsyShop.Listing(1, "Planter with SKU");
        listing1["description"] = "A lovely planter.\n\nWater weekly.";
        listing1["tags"] = new JsonArray("gardening", "ceramic");
        shop.Listings.Add(listing1);

        var listing2 = FakeEtsyShop.Listing(2, "Hanging Pot");
        listing2["description"] = "A lovely pot.\n\nWater weekly.";
        listing2["tags"] = new JsonArray("pottery", "indoor");
        shop.Listings.Add(listing2);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var blocks = shop.Provider.GetRequiredService<TextBlockService>();
        var (blockId, error) = await blocks.SaveAsync(null, "Plant Care", null, "Water weekly.", "tophattwaffle");
        Assert.Null(error);

        // Before linking: both are candidates because they contain "Water weekly."
        var candidates = await blocks.CandidatesAsync(blockId!.Value);
        Assert.Equal(2, candidates.Count);

        var cand1 = candidates.Single(c => c.ListingId == 1 || c.EtsyListingId == 1);
        Assert.Equal(FakeEtsyShop.ShopId, cand1.ShopId);
        Assert.Equal("active", cand1.State);
        Assert.False(cand1.IsMissingOnEtsy);
        Assert.Contains("gardening", cand1.Tags);

        // Link listing 1
        await blocks.LinkAsync(blockId.Value, [cand1.ListingId], "tophattwaffle");

        // Now listing 1 is in Uses, listing 2 is in Candidates
        var uses = await blocks.UsesAsync(blockId.Value);
        var use1 = Assert.Single(uses);
        Assert.Equal(cand1.ListingId, use1.ListingId);
        Assert.Equal(FakeEtsyShop.ShopId, use1.ShopId);
        Assert.Equal("active", use1.State);
        Assert.False(use1.IsMissingOnEtsy);
        Assert.Contains("ceramic", use1.Tags);

        // Test filtering on uses
        Assert.True(TemplateFilterMatcher.Matches(use1, new TemplateListingFilter { Text = "gardening" }));
        Assert.False(TemplateFilterMatcher.Matches(use1, new TemplateListingFilter { Text = "pottery" }));
        Assert.True(TemplateFilterMatcher.Matches(use1, new TemplateListingFilter { ShopId = FakeEtsyShop.ShopId }));
        Assert.False(TemplateFilterMatcher.Matches(use1, new TemplateListingFilter { ShopId = 999 }));
    }

    [Fact]
    public async Task VariationListService_PopulatesFilterFields_ForUsesAndCandidates()
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Varied Shirt");
        listing["tags"] = new JsonArray("apparel", "clothing");
        shop.Listings.Add(listing);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var lists = shop.Provider.GetRequiredService<VariationListService>();
        var (listId, error) = await lists.SaveAsync(null, "Sizes", [
            new VariationListOption(0, "S", true),
            new VariationListOption(0, "M", true)
        ], "tophattwaffle");
        Assert.Null(error);

        var candidates = await lists.CandidatesAsync(listId!.Value);
        foreach (var c in candidates)
        {
            Assert.True(c.ShopId > 0);
            Assert.False(string.IsNullOrEmpty(c.ShopName));
            Assert.False(string.IsNullOrEmpty(c.State));
        }

        var uses = await lists.UsesAsync(listId.Value);
        foreach (var u in uses)
        {
            Assert.True(u.ShopId > 0);
            Assert.False(string.IsNullOrEmpty(u.ShopName));
            Assert.False(string.IsNullOrEmpty(u.State));
        }
    }

    [Fact]
    public async Task PhotoTemplateService_PopulatesFilterFields_ForUsesAndCandidates()
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Art Print");
        listing["tags"] = new JsonArray("art", "wall decor");
        shop.Listings.Add(listing);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var photos = shop.Provider.GetRequiredService<PhotoTemplateService>();
        var imageStore = shop.Provider.GetRequiredService<ImageStore>();
        var filePath = await imageStore.SaveAsync([1, 2, 3], ".jpg");

        var (templateId, error) = await photos.SaveAsync(null, "Frame Dimensions", filePath, "Frame dimension diagram", 1, "tophattwaffle");
        Assert.Null(error);

        var candidates = await photos.CandidatesAsync(templateId!.Value);
        var cand = Assert.Single(candidates);
        Assert.Equal(FakeEtsyShop.ShopId, cand.ShopId);
        Assert.Equal("active", cand.State);
        Assert.False(cand.IsMissingOnEtsy);
        Assert.Contains("art", cand.Tags);

        // Filter matcher works on photo candidate
        Assert.True(TemplateFilterMatcher.Matches(cand, new TemplateListingFilter { Text = "print" }));
        Assert.True(TemplateFilterMatcher.Matches(cand, new TemplateListingFilter { Text = "wall decor" }));
        Assert.False(TemplateFilterMatcher.Matches(cand, new TemplateListingFilter { Text = "sculpture" }));

        // Link candidate
        await photos.LinkAsync(templateId.Value, [cand.ListingId], "tophattwaffle");

        var uses = await photos.UsesAsync(templateId.Value);
        var use = Assert.Single(uses);
        Assert.Equal(cand.ListingId, use.ListingId);
        Assert.Equal(FakeEtsyShop.ShopId, use.ShopId);
        Assert.Equal("active", use.State);
        Assert.False(use.IsMissingOnEtsy);
        Assert.Contains("art", use.Tags);
        Assert.True(TemplateFilterMatcher.Matches(use, new TemplateListingFilter { Text = "art" }));
    }
}
