using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Empirical stress tests and adversarial verification for Milestone 7 (R6: Template Table Listing Filters)
/// specifically targeting scoped bulk selection across filter transitions in TextBlock, VariationList,
/// and PhotoTemplate pages.
/// </summary>
public class ChallengerM7BulkSelectStressTests
{
    private sealed record MockListingRow : ITemplateListingItem
    {
        public int ListingId { get; init; }
        public string Title { get; init; } = "";
        public int ShopId { get; init; }
        public string ShopName { get; init; } = "";
        public long EtsyListingId { get; init; }
        public string State { get; init; } = "active";
        public bool IsMissingOnEtsy { get; init; }
        public IReadOnlyList<string> Skus { get; init; } = [];
        public IReadOnlyList<string> Tags { get; init; } = [];
        public long PropertyId { get; init; } = 0; // For variation candidates
    }

    /// <summary>
    /// Test harness mirroring the selection & filtering logic of TextBlockPage and PhotoTemplatePage.
    /// </summary>
    private sealed class SingleKeySelectionHarness
    {
        public List<MockListingRow> AllItems { get; } = [];
        public TemplateListingFilter Filter { get; set; } = new();
        public HashSet<int> Picked { get; } = [];

        public IReadOnlyList<MockListingRow> Visible =>
            AllItems.Where(i => TemplateFilterMatcher.Matches(i, Filter)).ToList();

        public bool? CandidateHeaderChecked
        {
            get
            {
                var visible = Visible.Select(c => c.ListingId).ToList();
                if (visible.Count == 0) return false;
                var pickedCount = visible.Count(Picked.Contains);
                if (pickedCount == 0) return false;
                if (pickedCount == visible.Count) return true;
                return null;
            }
        }

        public void TogglePickAllVisible()
        {
            var visible = Visible.Select(c => c.ListingId).ToList();
            if (visible.Count == 0) return;
            var allPicked = visible.All(Picked.Contains);
            if (allPicked)
                Picked.ExceptWith(visible);
            else
                Picked.UnionWith(visible);
        }

        public void Pick(int listingId, bool on)
        {
            if (on) Picked.Add(listingId); else Picked.Remove(listingId);
        }

        public int ActionButtonCount => Picked.Count;
        public bool ActionButtonDisabled => Picked.Count == 0;
        public string ActionButtonText => $"Link {Picked.Count} listing{(Picked.Count == 1 ? "" : "s")}";
    }

    /// <summary>
    /// Test harness mirroring the composite key selection & filtering logic of VariationListPage.
    /// </summary>
    private sealed class CompositeKeySelectionHarness
    {
        public List<MockListingRow> AllItems { get; } = [];
        public TemplateListingFilter Filter { get; set; } = new();
        public HashSet<(int ListingId, long PropertyId)> Picked { get; } = [];

        public IReadOnlyList<MockListingRow> Visible =>
            AllItems.Where(i => TemplateFilterMatcher.Matches(i, Filter)).ToList();

        public bool? CandidateHeaderChecked
        {
            get
            {
                var visible = Visible.Select(c => (c.ListingId, c.PropertyId)).ToList();
                if (visible.Count == 0) return false;
                var pickedCount = visible.Count(Picked.Contains);
                if (pickedCount == 0) return false;
                if (pickedCount == visible.Count) return true;
                return null;
            }
        }

        public void TogglePickAllVisible()
        {
            var visible = Visible.Select(c => (c.ListingId, c.PropertyId)).ToList();
            if (visible.Count == 0) return;
            var allPicked = visible.All(Picked.Contains);
            if (allPicked)
                Picked.ExceptWith(visible);
            else
                Picked.UnionWith(visible);
        }

        public void Pick((int ListingId, long PropertyId) key, bool on)
        {
            if (on) Picked.Add(key); else Picked.Remove(key);
        }

        public int ActionButtonCount => Picked.Count;
        public bool ActionButtonDisabled => Picked.Count == 0;
        public string ActionButtonText => $"Link {Picked.Count} listing{(Picked.Count == 1 ? "" : "s")}";
    }

    [Fact]
    public void SelectAll_UnderFilterA_SwitchToFilterB_PreservesPreviouslyPickedItems()
    {
        var harness = new SingleKeySelectionHarness();
        harness.AllItems.AddRange([
            new MockListingRow { ListingId = 1, Title = "Blue Ceramic Mug", ShopId = 10, State = "active" },
            new MockListingRow { ListingId = 2, Title = "Red Ceramic Bowl", ShopId = 10, State = "active" },
            new MockListingRow { ListingId = 3, Title = "Wooden Spoon", ShopId = 20, State = "active" },
            new MockListingRow { ListingId = 4, Title = "Wooden Fork", ShopId = 20, State = "active" },
        ]);

        // Filter A: Shop 10 (Mug, Bowl)
        harness.Filter = new TemplateListingFilter { ShopId = 10 };
        Assert.Equal(2, harness.Visible.Count);
        Assert.False(harness.CandidateHeaderChecked);
        Assert.True(harness.ActionButtonDisabled);

        // Select all under Filter A
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal([1, 2], harness.Picked.Order());
        Assert.Equal(2, harness.ActionButtonCount);
        Assert.False(harness.ActionButtonDisabled);

        // Switch to Filter B: Shop 20 (Spoon, Fork)
        harness.Filter = new TemplateListingFilter { ShopId = 20 };
        Assert.Equal(2, harness.Visible.Count);

        // In Filter B, none of the visible items (3, 4) are picked yet
        Assert.False(harness.CandidateHeaderChecked);
        // Previously picked items from Filter A MUST remain in set!
        Assert.Contains(1, harness.Picked);
        Assert.Contains(2, harness.Picked);
        Assert.DoesNotContain(3, harness.Picked);
        Assert.DoesNotContain(4, harness.Picked);
        Assert.Equal(2, harness.ActionButtonCount);
        Assert.False(harness.ActionButtonDisabled);
        Assert.Equal("Link 2 listings", harness.ActionButtonText);

        // Select all under Filter B as well
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal(4, harness.ActionButtonCount);
        Assert.Equal([1, 2, 3, 4], harness.Picked.Order());
    }

    [Fact]
    public void UnselectAll_UnderFilterB_RemovesOnlyVisibleInB_PreservesNonMatchingFromA()
    {
        var harness = new SingleKeySelectionHarness();
        harness.AllItems.AddRange([
            new MockListingRow { ListingId = 101, Title = "Planter Large", ShopId = 1, State = "active", Tags = ["pottery"] },
            new MockListingRow { ListingId = 102, Title = "Planter Small", ShopId = 1, State = "active", Tags = ["pottery"] },
            new MockListingRow { ListingId = 201, Title = "Linen Apron", ShopId = 2, State = "active", Tags = ["kitchen"] },
            new MockListingRow { ListingId = 202, Title = "Linen Napkin", ShopId = 2, State = "active", Tags = ["kitchen"] },
        ]);

        // Filter A: Shop 1 -> Select all
        harness.Filter = new TemplateListingFilter { ShopId = 1 };
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal([101, 102], harness.Picked.Order());

        // Switch to Filter B: Tag "kitchen" (Shop 2 items)
        harness.Filter = new TemplateListingFilter { Text = "kitchen" };
        Assert.Equal(2, harness.Visible.Count);
        Assert.False(harness.CandidateHeaderChecked);

        // Select all under Filter B as well
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal(4, harness.Picked.Count);

        // Now Unselect all under Filter B
        harness.TogglePickAllVisible();
        Assert.False(harness.CandidateHeaderChecked);

        // Visible items in Filter B (201, 202) must be removed
        Assert.DoesNotContain(201, harness.Picked);
        Assert.DoesNotContain(202, harness.Picked);

        // Items picked under Filter A (101, 102) that don't match Filter B MUST REMAIN INTACT!
        Assert.Contains(101, harness.Picked);
        Assert.Contains(102, harness.Picked);
        Assert.Equal(2, harness.ActionButtonCount);
        Assert.False(harness.ActionButtonDisabled);
        Assert.Equal("Link 2 listings", harness.ActionButtonText);

        // Switch back to Filter A: header should show true (all visible are selected)
        harness.Filter = new TemplateListingFilter { ShopId = 1 };
        Assert.True(harness.CandidateHeaderChecked);
    }

    [Fact]
    public void OverlappingFilter_UnselectAll_RemovesOnlyVisibleOverlap_PreservesExclusivePicks()
    {
        var harness = new SingleKeySelectionHarness();
        // Listing 1 matches both "Blue" and Shop 1
        // Listing 2 matches "Blue" only (Shop 2)
        // Listing 3 matches Shop 1 only ("Red")
        harness.AllItems.AddRange([
            new MockListingRow { ListingId = 1, Title = "Blue Vase", ShopId = 1, State = "active" },
            new MockListingRow { ListingId = 2, Title = "Blue Mug", ShopId = 2, State = "active" },
            new MockListingRow { ListingId = 3, Title = "Red Plate", ShopId = 1, State = "active" },
        ]);

        // Filter A: Text "Blue" (Listings 1 & 2)
        harness.Filter = new TemplateListingFilter { Text = "Blue" };
        harness.TogglePickAllVisible();
        Assert.Equal([1, 2], harness.Picked.Order());

        // Filter B: Shop 1 (Listings 1 & 3)
        harness.Filter = new TemplateListingFilter { ShopId = 1 };
        // Listing 1 is picked, 3 is not picked => Indeterminate (null)
        Assert.Null(harness.CandidateHeaderChecked);

        // Clicking toggle when indeterminate: selects all visible in B (1 & 3)
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal([1, 2, 3], harness.Picked.Order());

        // Now Unselect all under Filter B
        harness.TogglePickAllVisible();
        Assert.False(harness.CandidateHeaderChecked);

        // Listing 1 & 3 removed; Listing 2 (from Filter A only) MUST remain picked!
        Assert.DoesNotContain(1, harness.Picked);
        Assert.DoesNotContain(3, harness.Picked);
        Assert.Contains(2, harness.Picked);
        Assert.Single(harness.Picked);
        Assert.Equal(1, harness.ActionButtonCount);
        Assert.Equal("Link 1 listing", harness.ActionButtonText);
    }

    [Fact]
    public void ZeroVisibleItems_HeaderCheckboxIsUnselected_AndActionButtonReflectsTotalPickedCount()
    {
        var harness = new SingleKeySelectionHarness();
        harness.AllItems.AddRange([
            new MockListingRow { ListingId = 10, Title = "Gold Ring", ShopId = 1, State = "active" },
            new MockListingRow { ListingId = 20, Title = "Silver Ring", ShopId = 1, State = "active" },
        ]);

        // Select all under Shop 1
        harness.Filter = new TemplateListingFilter { ShopId = 1 };
        harness.TogglePickAllVisible();
        Assert.Equal(2, harness.ActionButtonCount);
        Assert.Equal([10, 20], harness.Picked.Order());

        // Switch to filter with ZERO matching items
        harness.Filter = new TemplateListingFilter { Text = "NON_EXISTENT_QUERY_XYZ" };
        Assert.Empty(harness.Visible);

        // Verification 1: Header checkbox is unselected (false) when zero items are visible
        Assert.False(harness.CandidateHeaderChecked);

        // Verification 2: TogglePickAllVisible is safe no-op and does not clear or alter Picked
        harness.TogglePickAllVisible();
        Assert.Equal([10, 20], harness.Picked.Order());
        Assert.False(harness.CandidateHeaderChecked);

        // Verification 3: Action button reflects total picked count (2) and is not disabled
        Assert.Equal(2, harness.ActionButtonCount);
        Assert.False(harness.ActionButtonDisabled);
        Assert.Equal("Link 2 listings", harness.ActionButtonText);

        // When nothing is picked at all and zero items visible:
        harness.Picked.Clear();
        Assert.False(harness.CandidateHeaderChecked);
        Assert.Equal(0, harness.ActionButtonCount);
        Assert.True(harness.ActionButtonDisabled);
        Assert.Equal("Link 0 listings", harness.ActionButtonText);
    }

    [Fact]
    public void VariationList_CompositeKeySelection_MaintainsPerVariationScopingAcrossFilters()
    {
        var harness = new CompositeKeySelectionHarness();
        // Listing 1 has two variations: Size (100) and Color (200)
        // Listing 2 has one variation: Size (100)
        // Listing 3 has one variation: Size (100) in Shop 2
        harness.AllItems.AddRange([
            new MockListingRow { ListingId = 1, Title = "T-Shirt", PropertyId = 100, ShopId = 1, State = "active" },
            new MockListingRow { ListingId = 1, Title = "T-Shirt", PropertyId = 200, ShopId = 1, State = "active" },
            new MockListingRow { ListingId = 2, Title = "Hoodie", PropertyId = 100, ShopId = 1, State = "active" },
            new MockListingRow { ListingId = 3, Title = "Cap", PropertyId = 100, ShopId = 2, State = "active" },
        ]);

        // Filter A: Shop 1 (Listings 1 & 2, 3 rows total)
        harness.Filter = new TemplateListingFilter { ShopId = 1 };
        Assert.Equal(3, harness.Visible.Count);

        // Pick all under Shop 1
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal(3, harness.ActionButtonCount);
        Assert.Contains((1, 100), harness.Picked);
        Assert.Contains((1, 200), harness.Picked);
        Assert.Contains((2, 100), harness.Picked);

        // Switch to Filter B: Shop 2 (Cap, 1 row)
        harness.Filter = new TemplateListingFilter { ShopId = 2 };
        Assert.Single(harness.Visible);
        Assert.False(harness.CandidateHeaderChecked);
        // Previous 3 selections preserved
        Assert.Equal(3, harness.ActionButtonCount);

        // Toggle pick on Shop 2: now all 4 picked
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal(4, harness.ActionButtonCount);
        Assert.Contains((3, 100), harness.Picked);

        // Unselect Shop 2: only (3, 100) removed
        harness.TogglePickAllVisible();
        Assert.False(harness.CandidateHeaderChecked);
        Assert.DoesNotContain((3, 100), harness.Picked);

        // Shop 1 variations remain intact
        Assert.Contains((1, 100), harness.Picked);
        Assert.Contains((1, 200), harness.Picked);
        Assert.Contains((2, 100), harness.Picked);
        Assert.Equal(3, harness.ActionButtonCount);

        // Filter to zero matches
        harness.Filter = new TemplateListingFilter { Text = "UNKNOWN_PRODUCT" };
        Assert.Empty(harness.Visible);
        Assert.False(harness.CandidateHeaderChecked);
        Assert.Equal(3, harness.ActionButtonCount);
        Assert.False(harness.ActionButtonDisabled);
    }

    [Fact]
    public void StatusFilter_Transitions_MaintainScopedSelections()
    {
        var harness = new SingleKeySelectionHarness();
        harness.AllItems.AddRange([
            new MockListingRow { ListingId = 1, Title = "Draft Item 1", State = "draft", IsMissingOnEtsy = false },
            new MockListingRow { ListingId = 2, Title = "Draft Item 2", State = "draft", IsMissingOnEtsy = false },
            new MockListingRow { ListingId = 3, Title = "Active Item", State = "active", IsMissingOnEtsy = false },
            new MockListingRow { ListingId = 4, Title = "Gone Item", State = "active", IsMissingOnEtsy = true },
        ]);

        // Filter: Draft
        harness.Filter = new TemplateListingFilter { Status = "draft" };
        Assert.Equal(2, harness.Visible.Count);
        harness.TogglePickAllVisible();
        Assert.Equal([1, 2], harness.Picked.Order());

        // Filter: Missing (Gone from Etsy)
        harness.Filter = new TemplateListingFilter { Status = "missing" };
        var visibleMissing = Assert.Single(harness.Visible);
        Assert.Equal(4, visibleMissing.ListingId);
        Assert.False(harness.CandidateHeaderChecked);

        // Pick missing item
        harness.TogglePickAllVisible();
        Assert.True(harness.CandidateHeaderChecked);
        Assert.Equal([1, 2, 4], harness.Picked.Order());

        // Switch to "all" status (should show non-missing items 1, 2, 3)
        harness.Filter = new TemplateListingFilter { Status = "all" };
        Assert.Equal(3, harness.Visible.Count);
        // 1 & 2 are picked, 3 is not -> indeterminate
        Assert.Null(harness.CandidateHeaderChecked);

        // Unselect visible in Draft
        harness.Filter = new TemplateListingFilter { Status = "draft" };
        Assert.True(harness.CandidateHeaderChecked);
        harness.TogglePickAllVisible();
        Assert.False(harness.CandidateHeaderChecked);

        // 1 & 2 removed, 4 (missing) still picked!
        Assert.DoesNotContain(1, harness.Picked);
        Assert.DoesNotContain(2, harness.Picked);
        Assert.Contains(4, harness.Picked);
        Assert.Equal(1, harness.ActionButtonCount);
    }

    [Fact]
    public void StressTest_1000Items_RandomFilterTransitions_PreserveInvariants()
    {
        var harness = new SingleKeySelectionHarness();
        var rng = new Random(42);
        var states = new[] { "active", "draft", "expired", "sold_out", "inactive" };
        var tagsPool = new[] { "tagA", "tagB", "tagC", "tagD", "tagE" };

        for (var i = 1; i <= 1000; i++)
        {
            var isMissing = i % 25 == 0;
            harness.AllItems.Add(new MockListingRow
            {
                ListingId = i,
                Title = $"Listing {i} {(i % 2 == 0 ? "Alpha" : "Beta")}",
                ShopId = (i % 3) + 1,
                ShopName = $"Shop {(i % 3) + 1}",
                EtsyListingId = 1000000000L + i,
                State = states[i % states.Length],
                IsMissingOnEtsy = isMissing,
                Skus = [$"SKU-{i}"],
                Tags = [tagsPool[i % tagsPool.Length]],
            });
        }

        var sampleFilters = new[]
        {
            new TemplateListingFilter { ShopId = 1 },
            new TemplateListingFilter { ShopId = 2 },
            new TemplateListingFilter { ShopId = 3 },
            new TemplateListingFilter { Text = "Alpha" },
            new TemplateListingFilter { Text = "Beta" },
            new TemplateListingFilter { Status = "draft" },
            new TemplateListingFilter { Status = "active" },
            new TemplateListingFilter { Status = "missing" },
            new TemplateListingFilter { Text = "tagA" },
            new TemplateListingFilter { Text = "SKU-999" },
            new TemplateListingFilter { Text = "NON_EXISTENT" },
            new TemplateListingFilter { Status = "all" },
        };

        for (var cycle = 0; cycle < 50; cycle++)
        {
            var filter = sampleFilters[rng.Next(sampleFilters.Length)];
            harness.Filter = filter;
            var visibleIds = harness.Visible.Select(v => v.ListingId).ToHashSet();

            // Snapshot non-visible picked items before action
            var nonVisiblePickedBefore = harness.Picked.Where(p => !visibleIds.Contains(p)).ToHashSet();

            if (visibleIds.Count == 0)
            {
                Assert.False(harness.CandidateHeaderChecked);
                harness.TogglePickAllVisible();
                Assert.False(harness.CandidateHeaderChecked);
                // No changes should have occurred
                Assert.True(nonVisiblePickedBefore.SetEquals(harness.Picked));
            }
            else
            {
                var action = rng.Next(2);
                if (action == 0)
                {
                    // Toggle pick all visible
                    harness.TogglePickAllVisible();
                }
                else
                {
                    // Pick/unpick a random visible item
                    var randomVisible = visibleIds.ElementAt(rng.Next(visibleIds.Count));
                    harness.Pick(randomVisible, !harness.Picked.Contains(randomVisible));
                }

                // INVARIANT 1: Non-visible picked items MUST NOT be altered by any visible action!
                var nonVisiblePickedAfter = harness.Picked.Where(p => !visibleIds.Contains(p)).ToHashSet();
                Assert.True(nonVisiblePickedBefore.SetEquals(nonVisiblePickedAfter),
                    $"Cycle {cycle}: non-visible picked items were modified during filter transition!");

                // INVARIANT 2: Action button count equals total picked count
                Assert.Equal(harness.Picked.Count, harness.ActionButtonCount);

                // INVARIANT 3: Header state is mathematically consistent
                var visiblePickedCount = visibleIds.Count(harness.Picked.Contains);
                if (visiblePickedCount == 0)
                    Assert.False(harness.CandidateHeaderChecked);
                else if (visiblePickedCount == visibleIds.Count)
                    Assert.True(harness.CandidateHeaderChecked);
                else
                    Assert.Null(harness.CandidateHeaderChecked);
            }
        }
    }

    [Fact]
    public async Task ServiceIntegration_TextBlock_FilteringAndSelectionState_EndToEnd()
    {
        var shop = new FakeEtsyShop();

        // Listing 1: Shop 1, active, contains text
        var l1 = FakeEtsyShop.Listing(1, "Handmade Blue Pottery Mug");
        l1["description"] = "Features:\nDishwasher safe ceramic.\nEnjoy your coffee.";
        l1["tags"] = new JsonArray("ceramic", "blue");
        shop.Listings.Add(l1);

        // Listing 2: Shop 1, draft, contains text
        var l2 = FakeEtsyShop.Listing(2, "Handmade Red Pottery Mug", state: "draft");
        l2["description"] = "Features:\nDishwasher safe ceramic.\nEnjoy your coffee.";
        l2["tags"] = new JsonArray("ceramic", "red");
        shop.Listings.Add(l2);

        // Listing 3: Shop 1, active, does not contain text
        var l3 = FakeEtsyShop.Listing(3, "Glass Tumbler");
        l3["description"] = "Delicate glass cup.";
        l3["tags"] = new JsonArray("glassware");
        shop.Listings.Add(l3);

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var blocks = shop.Provider.GetRequiredService<TextBlockService>();
        var (blockId, _) = await blocks.SaveAsync(null, "Dishwasher Notice", null, "Dishwasher safe ceramic.", "tophattwaffle");
        Assert.NotNull(blockId);

        var candidates = await blocks.CandidatesAsync(blockId.Value);
        Assert.Equal(2, candidates.Count);

        // Filter by text "Blue": only candidate 1
        var filterBlue = new TemplateListingFilter { Text = "Blue" };
        var visibleBlue = candidates.Where(c => TemplateFilterMatcher.Matches(c, filterBlue)).ToList();
        var cand1 = Assert.Single(visibleBlue);
        Assert.Equal("Handmade Blue Pottery Mug", cand1.Title);

        // Filter by status "draft": only candidate 2
        var filterDraft = new TemplateListingFilter { Status = "draft" };
        var visibleDraft = candidates.Where(c => TemplateFilterMatcher.Matches(c, filterDraft)).ToList();
        var cand2 = Assert.Single(visibleDraft);
        Assert.Equal("Handmade Red Pottery Mug", cand2.Title);

        // Simulate selection across filter transition
        var picked = new HashSet<int>();

        // Under Blue filter: select all
        picked.UnionWith(visibleBlue.Select(c => c.ListingId));
        Assert.Equal([cand1.ListingId], picked);

        // Switch to Draft filter: cand1 remains picked, visible cand2 is not yet picked
        Assert.Contains(cand1.ListingId, picked);
        Assert.DoesNotContain(cand2.ListingId, picked);

        // Select all under Draft filter
        picked.UnionWith(visibleDraft.Select(c => c.ListingId));
        Assert.Equal(new[] { cand1.ListingId, cand2.ListingId }.Order(), picked.Order());

        // Unselect all under Blue filter
        picked.ExceptWith(visibleBlue.Select(c => c.ListingId));
        // Cand1 removed, cand2 (from Draft filter) remains intact!
        Assert.DoesNotContain(cand1.ListingId, picked);
        Assert.Contains(cand2.ListingId, picked);

        // Link the remaining picked candidate (cand2) via service
        var linkedCount = await blocks.LinkAsync(blockId.Value, picked, "tophattwaffle");
        Assert.Equal(1, linkedCount);

        var uses = await blocks.UsesAsync(blockId.Value);
        var use = Assert.Single(uses);
        Assert.Equal(cand2.ListingId, use.ListingId);
    }
}
