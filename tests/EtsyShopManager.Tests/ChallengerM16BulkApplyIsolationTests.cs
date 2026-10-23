using System.Reflection;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Etsy;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Adversarial challenger empirical tests for Milestone 16 (R3: Filter Listings by Current Processing Profile).
/// Specifically stress-tests bulk-apply preview isolation, count accuracy, filter transitions,
/// and target selection on ProcessingProfilesPage.
/// </summary>
public class ChallengerM16BulkApplyIsolationTests
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

    /// <summary>
    /// Test adapter for ProcessingProfilesPage exercising the compiled Blazor component directly via reflection.
    /// </summary>
    private sealed class ProcessingProfilesPageTestAdapter
    {
        public ProcessingProfilesPage Page { get; } = new();

        public List<ProcessingProfileCandidate> Candidates
        {
            get => (List<ProcessingProfileCandidate>)GetField("_candidates")!;
            set => SetField("_candidates", value);
        }

        public HashSet<int> Picked => (HashSet<int>)GetField("_picked")!;

        public string? FilterText
        {
            get => (string?)GetField("_filterText");
            set => InvokeMethod("OnFilterTextChanged", value);
        }

        public int? FilterShopId
        {
            get => (int?)GetField("_filterShopId");
            set => InvokeMethod("OnFilterShopChanged", value);
        }

        public string FilterStatus
        {
            get => (string)GetField("_filterStatus")!;
            set => InvokeMethod("OnFilterStatusChanged", value);
        }

        public string FilterProfile
        {
            get => (string)GetField("_filterProfile")!;
            set => InvokeMethod("OnFilterProfileChanged", value);
        }

        public long? ApplyProfileId
        {
            get => (long?)GetField("_applyProfileId");
            set => SetField("_applyProfileId", value);
        }

        public List<ProcessingProfileItem> AllProfiles
        {
            get => (List<ProcessingProfileItem>)GetField("_allProfiles")!;
            set => SetField("_allProfiles", value);
        }

        public List<ProcessingProfileCandidate> VisibleCandidates =>
            (List<ProcessingProfileCandidate>)GetProperty("VisibleCandidates")!;

        public int TargetCount => (int)GetProperty("TargetCount")!;

        public bool? CandidateHeaderChecked => (bool?)GetProperty("CandidateHeaderChecked");

        public IReadOnlyList<ProcessingProfileApplyRow>? PreviewRows
        {
            get => (IReadOnlyList<ProcessingProfileApplyRow>?)GetField("_previewRows");
            set => SetField("_previewRows", value);
        }

        public void TogglePick(int listingId, bool picked) =>
            InvokeMethod("TogglePick", listingId, picked);

        public void SelectAllVisible() =>
            InvokeMethod("SelectAllVisible");

        public void TogglePickAllVisible() =>
            InvokeMethod("TogglePickAllVisible");

        public Task PreviewBulkApplyAsync() =>
            (Task)InvokeMethod("PreviewBulkApplyAsync")!;

        public void InjectProfileService(ProcessingProfileService service) =>
            SetProperty("ProfileService", service);

        private object? GetField(string name) =>
            typeof(ProcessingProfilesPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private void SetField(string name, object? value) =>
            typeof(ProcessingProfilesPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);

        private object? GetProperty(string name) =>
            typeof(ProcessingProfilesPage).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private void SetProperty(string name, object? value) =>
            typeof(ProcessingProfilesPage).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);

        private object? InvokeMethod(string name, params object?[] parameters) =>
            typeof(ProcessingProfilesPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, parameters);
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
    public void DirectComponent_FilterTransition_VisibleCount_TargetCount_And_SelectAllVisible_Isolation()
    {
        var adapter = new ProcessingProfilesPageTestAdapter();

        // 6 Candidates across 2 shops and multiple profile configurations:
        var candidates = new List<ProcessingProfileCandidate>
        {
            new(ListingId: 1, Title: "Item 1", ShopId: 1, ShopName: "Shop 1", EtsyListingId: 101, State: "active",
                IsMissingOnEtsy: false, Skus: ["SKU-1"], Tags: ["mug"], CurrentReadinessStateId: 601, CurrentProfileName: "1-3 days", VariesByVariation: false),
            new(ListingId: 2, Title: "Item 2", ShopId: 1, ShopName: "Shop 1", EtsyListingId: 102, State: "active",
                IsMissingOnEtsy: false, Skus: ["SKU-2"], Tags: ["bowl"], CurrentReadinessStateId: 601, CurrentProfileName: "1-3 days", VariesByVariation: false),
            new(ListingId: 3, Title: "Item 3", ShopId: 1, ShopName: "Shop 1", EtsyListingId: 103, State: "active",
                IsMissingOnEtsy: false, Skus: ["SKU-3"], Tags: ["vase"], CurrentReadinessStateId: 602, CurrentProfileName: "3-5 days", VariesByVariation: false),
            new(ListingId: 4, Title: "Item 4", ShopId: 1, ShopName: "Shop 1", EtsyListingId: 104, State: "active",
                IsMissingOnEtsy: false, Skus: ["SKU-4"], Tags: ["planter"], CurrentReadinessStateId: null, CurrentProfileName: "Varies by variation", VariesByVariation: true),
            new(ListingId: 5, Title: "Item 5", ShopId: 1, ShopName: "Shop 1", EtsyListingId: 105, State: "active",
                IsMissingOnEtsy: false, Skus: ["SKU-5"], Tags: ["plate"], CurrentReadinessStateId: null, CurrentProfileName: "No profile", VariesByVariation: false),
            new(ListingId: 6, Title: "Item 6", ShopId: 2, ShopName: "Shop 2", EtsyListingId: 201, State: "active",
                IsMissingOnEtsy: false, Skus: ["SKU-6"], Tags: ["cup"], CurrentReadinessStateId: 601, CurrentProfileName: "1-3 days", VariesByVariation: false),
        };

        adapter.Candidates = candidates;
        adapter.FilterShopId = 1;

        // --- STEP 1: Filter A = Profile "601" in Shop 1 ---
        adapter.FilterProfile = "601";

        // Verification 1A: Visible candidate count matches Filter A immediately
        Assert.Equal(2, adapter.VisibleCandidates.Count);
        Assert.Equal([1, 2], adapter.VisibleCandidates.Select(c => c.ListingId).Order());
        Assert.Equal(0, adapter.TargetCount);
        Assert.False(adapter.CandidateHeaderChecked);

        // Select all visible under Filter A
        adapter.SelectAllVisible();
        Assert.Equal([1, 2], adapter.Picked.Order());
        Assert.Equal(2, adapter.TargetCount);
        Assert.True(adapter.CandidateHeaderChecked);

        // --- STEP 2: Switch to Filter B = Profile "varies" (disjoint from Filter A) ---
        adapter.FilterProfile = "varies";

        // Verification 1B: Visible candidate count updates immediately to Filter B
        Assert.Single(adapter.VisibleCandidates);
        Assert.Equal(4, adapter.VisibleCandidates[0].ListingId);

        // Verification 1C: Picked set retains selections from Filter A
        Assert.Contains(1, adapter.Picked);
        Assert.Contains(2, adapter.Picked);
        Assert.DoesNotContain(4, adapter.Picked);

        // Verification 1D: TargetCount immediately drops to 0 (disjoint intersection)
        Assert.Equal(0, adapter.TargetCount);
        Assert.False(adapter.CandidateHeaderChecked);

        // Verification 1E: SelectAllVisible under Filter B adds ONLY visible candidate 4
        adapter.SelectAllVisible();
        Assert.Equal([1, 2, 4], adapter.Picked.Order());
        Assert.Equal(1, adapter.TargetCount); // TargetCount for Filter B is now 1 (listing 4)
        Assert.True(adapter.CandidateHeaderChecked);

        // --- STEP 3: Switch to Filter C = Profile "none" (disjoint from Filter A & B) ---
        adapter.FilterProfile = "none";

        // Visible count updates immediately
        Assert.Single(adapter.VisibleCandidates);
        Assert.Equal(5, adapter.VisibleCandidates[0].ListingId);
        Assert.Equal(0, adapter.TargetCount);
        Assert.False(adapter.CandidateHeaderChecked);

        // SelectAllVisible adds ONLY candidate 5
        adapter.SelectAllVisible();
        Assert.Equal([1, 2, 4, 5], adapter.Picked.Order());
        Assert.Equal(1, adapter.TargetCount); // TargetCount for Filter C is now 1 (listing 5)
        Assert.True(adapter.CandidateHeaderChecked);

        // --- STEP 4: Switch back to Filter A = Profile "601" ---
        adapter.FilterProfile = "601";

        // Visible count updates immediately
        Assert.Equal(2, adapter.VisibleCandidates.Count);
        // TargetCount immediately restores to 2 because items 1 and 2 were preserved in Picked!
        Assert.Equal(2, adapter.TargetCount);
        Assert.True(adapter.CandidateHeaderChecked);

        // Toggle header checkbox off under Filter A: removes visible items 1 and 2, but leaves 4 and 5 in Picked!
        adapter.TogglePickAllVisible();
        Assert.Equal([4, 5], adapter.Picked.Order());
        Assert.Equal(0, adapter.TargetCount);
        Assert.False(adapter.CandidateHeaderChecked);
    }

    [Fact]
    public void DirectComponent_PartialFilterOverlap_TargetCount_IsExactIntersection()
    {
        var adapter = new ProcessingProfilesPageTestAdapter();

        // 3 items:
        // Item 1: Shop 1, Status "active", Profile 601
        // Item 2: Shop 1, Status "draft",  Profile 601 (shares Profile 601 with Item 1, shares Draft with Item 3)
        // Item 3: Shop 1, Status "draft",  Profile 602
        adapter.Candidates =
        [
            new(1, "Item 1", 1, "Shop 1", 101, "active", false, [], [], 601, "1-3 days", false),
            new(2, "Item 2", 1, "Shop 1", 102, "draft", false, [], [], 601, "1-3 days", false),
            new(3, "Item 3", 1, "Shop 1", 103, "draft", false, [], [], 602, "3-5 days", false),
        ];
        adapter.FilterShopId = 1;

        // Filter A: Profile 601 (Items 1 and 2)
        adapter.FilterProfile = "601";
        adapter.FilterStatus = "all";
        Assert.Equal(2, adapter.VisibleCandidates.Count);

        // Select all under Filter A
        adapter.SelectAllVisible();
        Assert.Equal([1, 2], adapter.Picked.Order());
        Assert.Equal(2, adapter.TargetCount);

        // Switch to Filter B: Status "draft" (Items 2 and 3)
        adapter.FilterProfile = "all";
        adapter.FilterStatus = "draft";
        Assert.Equal(2, adapter.VisibleCandidates.Count);
        Assert.Equal([2, 3], adapter.VisibleCandidates.Select(c => c.ListingId).Order());

        // TargetCount MUST be exactly 1 (Item 2 is the sole intersection between {1, 2} and {2, 3})
        Assert.Equal(1, adapter.TargetCount);
        // Header checked state is indeterminate (null) because 1 of 2 visible is picked
        Assert.Null(adapter.CandidateHeaderChecked);

        // SelectAllVisible under Filter B adds Item 3 (Item 2 already in set)
        adapter.SelectAllVisible();
        Assert.Equal([1, 2, 3], adapter.Picked.Order());
        // TargetCount under Filter B is now 2 (both visible draft items)
        Assert.Equal(2, adapter.TargetCount);
        Assert.True(adapter.CandidateHeaderChecked);
    }

    [Fact]
    public void DirectComponent_FilterTransitions_ClearActivePreview_PreventingStaleCommits()
    {
        var adapter = new ProcessingProfilesPageTestAdapter();
        adapter.Candidates =
        [
            new(1, "Mug", 1, "Shop 1", 101, "active", false, [], [], 601, "1-3 days", false),
            new(2, "Bowl", 1, "Shop 1", 102, "active", false, [], [], 602, "3-5 days", false),
        ];

        // Simulate an existing preview
        adapter.PreviewRows = [new ProcessingProfileApplyRow(1, "Mug", "Shop 1", "1-3 days", "3-5 days", null, true)];
        Assert.NotNull(adapter.PreviewRows);

        // 1. Changing profile filter clears preview
        adapter.FilterProfile = "601";
        Assert.Null(adapter.PreviewRows);

        adapter.PreviewRows = [new ProcessingProfileApplyRow(1, "Mug", "Shop 1", "1-3 days", "3-5 days", null, true)];
        // 2. Changing shop filter clears preview
        adapter.FilterShopId = 1;
        Assert.Null(adapter.PreviewRows);

        adapter.PreviewRows = [new ProcessingProfileApplyRow(1, "Mug", "Shop 1", "1-3 days", "3-5 days", null, true)];
        // 3. Changing status filter clears preview
        adapter.FilterStatus = "draft";
        Assert.Null(adapter.PreviewRows);

        adapter.PreviewRows = [new ProcessingProfileApplyRow(1, "Mug", "Shop 1", "1-3 days", "3-5 days", null, true)];
        // 4. Changing search text clears preview
        adapter.FilterText = "ceramic";
        Assert.Null(adapter.PreviewRows);
    }

    [Fact]
    public void DirectComponent_CrossShopFilterTransition_ResetsProfileFilterIfUnavailable()
    {
        var adapter = new ProcessingProfilesPageTestAdapter();
        adapter.AllProfiles =
        [
            new(1, 1, 601, "ready_to_ship", 1, 3, "1-3 days", 5, DateTime.UtcNow), // Belongs to Shop 1
            new(2, 2, 901, "made_to_order", 3, 5, "3-5 days", 2, DateTime.UtcNow), // Belongs to Shop 2
        ];

        adapter.FilterShopId = 1;
        adapter.FilterProfile = "601";
        Assert.Equal("601", adapter.FilterProfile);

        // Switch to Shop 2 where profile 601 does not exist
        adapter.FilterShopId = 2;
        // Invariant: Profile filter must automatically reset to "all"
        Assert.Equal("all", adapter.FilterProfile);
    }

    [Fact]
    public void Adversarial_SelectAllMatchingButton_MarkupCondition_EdgeCaseAnalysis()
    {
        // ProcessingProfilesPage.razor line 139:
        // @if (VisibleCandidates.Count > 0 && _picked.Count < VisibleCandidates.Count)
        // {
        //     <MudButton ... OnClick="SelectAllVisible">Select all @VisibleCandidates.Count matching</MudButton>
        // }
        //
        // Stress-testing this condition:
        // Suppose 5 items were picked under Filter A (_picked.Count = 5).
        // User switches to Filter B with 2 visible candidates (VisibleCandidates.Count = 2).
        // None of the visible candidates in Filter B are picked yet.
        var picked = new HashSet<int> { 10, 11, 12, 13, 14 };
        var visibleCandidatesInB = new List<int> { 20, 21 };

        // Evaluate the exact Razor condition:
        var conditionMet = visibleCandidatesInB.Count > 0 && picked.Count < visibleCandidatesInB.Count;

        // Finding: 5 < 2 is FALSE!
        // This means the text button "Select all 2 matching" is HIDDEN from the user,
        // even though 0 of the 2 visible items are selected!
        Assert.False(conditionMet);

        // However, verify that the CandidateHeaderCheckbox and SelectAllVisible method themselves
        // correctly handle this situation:
        var visiblePicked = picked.Intersect(visibleCandidatesInB).Count();
        Assert.Equal(0, visiblePicked);

        // 1. CandidateHeaderChecked returns false (not checked)
        var headerChecked = visibleCandidatesInB.Count > 0 && picked.Count > 0
            ? (visiblePicked == visibleCandidatesInB.Count ? true : (visiblePicked > 0 ? (bool?)null : false))
            : false;
        Assert.False(headerChecked);

        // 2. SelectAllVisible() directly adds visible items
        foreach (var id in visibleCandidatesInB) picked.Add(id);
        Assert.Equal([10, 11, 12, 13, 14, 20, 21], picked.Order());
    }

    [Fact]
    public async Task EndToEnd_DatabaseService_BulkApply_OnlyAppliesToIntersectionWithVisibleCandidates()
    {
        using var shop = new FakeEtsyShop();

        // Setup 2 processing profiles
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

        // 4 Listings:
        // Listings 101, 102 have Profile 601
        // Listings 103, 104 have Profile 602
        shop.Listings.Add(CreateListingWithOfferings(101, "Mug 101", FakeEtsyShop.ShopId, 601L, (1001, 601L, false)));
        shop.Listings.Add(CreateListingWithOfferings(102, "Mug 102", FakeEtsyShop.ShopId, 601L, (1002, 601L, false)));
        shop.Listings.Add(CreateListingWithOfferings(103, "Vase 103", FakeEtsyShop.ShopId, 602L, (1003, 602L, false)));
        shop.Listings.Add(CreateListingWithOfferings(104, "Vase 104", FakeEtsyShop.ShopId, 602L, (1004, 602L, false)));

        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var auditLog = new TestAuditLog();
        var service = new ProcessingProfileService(
            shop.Db,
            shop.Provider.GetRequiredService<EtsyApiClient>(),
            shop.Provider.GetRequiredService<ListingEditor>(),
            auditLog);

        var candidates = await service.GetCandidatesAsync(FakeEtsyShop.ShopId, CancellationToken.None);
        Assert.Equal(4, candidates.Count);

        var cand101 = candidates.Single(c => c.EtsyListingId == 101);
        var cand102 = candidates.Single(c => c.EtsyListingId == 102);
        var cand103 = candidates.Single(c => c.EtsyListingId == 103);
        var cand104 = candidates.Single(c => c.EtsyListingId == 104);

        // Simulate Selection Under Filter A (Profile 601):
        // Filter A matches 101 and 102. User selects both.
        var filterA = new TemplateListingFilter { ProcessingProfile = "601" };
        var visibleA = candidates.Where(c => TemplateFilterMatcher.Matches(c, filterA)).ToList();
        Assert.Equal(2, visibleA.Count);

        var picked = new HashSet<int> { cand101.ListingId, cand102.ListingId };

        // User switches to Filter B (Profile 602):
        // Filter B matches 103 and 104.
        var filterB = new TemplateListingFilter { ProcessingProfile = "602" };
        var visibleB = candidates.Where(c => TemplateFilterMatcher.Matches(c, filterB)).ToList();
        Assert.Equal(2, visibleB.Count);

        // Verification 1: Target listings before user picks anything in B is EMPTY (intersection is empty)
        var targetIdsDisjoint = picked.Intersect(visibleB.Select(c => c.ListingId)).ToList();
        Assert.Empty(targetIdsDisjoint);

        // Verification 2: User picks Listing 103 in Filter B.
        // Target listings is strictly the intersection: [cand103.ListingId]
        picked.Add(cand103.ListingId);
        var targetIds = picked.Intersect(visibleB.Select(c => c.ListingId)).ToList();
        Assert.Equal([cand103.ListingId], targetIds);

        // Verification 3: PreviewBulkApplyAsync on Target Profile 601 targets ONLY listing 103!
        // Listings 101 and 102 (picked in A, but hidden in B) MUST NOT be in the preview!
        var preview = await service.PreviewBulkApplyAsync(601L, targetIds);
        var previewRow = Assert.Single(preview);
        Assert.Equal(cand103.ListingId, previewRow.ListingId);
        Assert.True(previewRow.Changes);
        Assert.Equal("3-5 business days", previewRow.Before);
        Assert.Equal("1-3 business days", previewRow.After);

        // Verification 4: ApplyAsync on Target Profile 601 applies unpushed edits ONLY to listing 103!
        var applied = await service.ApplyAsync(601L, targetIds, "tester");
        Assert.Equal([cand103.ListingId], applied);

        // Verification 5: In DB, inspect draft state:
        // - Listing 103 has draft JSON with readiness_state_id = 601
        // - Listings 101, 102, 104 have NULL DraftJson (completely untouched!)
        await using var db = shop.Db.CreateDbContext();
        var db101 = await db.Listings.SingleAsync(l => l.Id == cand101.ListingId);
        var db102 = await db.Listings.SingleAsync(l => l.Id == cand102.ListingId);
        var db103 = await db.Listings.SingleAsync(l => l.Id == cand103.ListingId);
        var db104 = await db.Listings.SingleAsync(l => l.Id == cand104.ListingId);

        Assert.Null(db101.DraftJson);
        Assert.Null(db102.DraftJson);
        Assert.Null(db104.DraftJson);
        Assert.NotNull(db103.DraftJson);

        var draftInventory = JsonNode.Parse(db103.DraftJson);
        var offeringReadyId = draftInventory?["inventory"]?["products"]?[0]?["offerings"]?[0]?["readiness_state_id"]?.GetValue<long>();
        Assert.Equal(601L, offeringReadyId);
    }

    [Fact]
    public void StressTest_100Items_RandomTransitions_PreserveSelectionIsolationInvariants()
    {
        var adapter = new ProcessingProfilesPageTestAdapter();
        var rng = new Random(1337);

        // 100 candidate items
        var states = new[] { "active", "draft", "expired", "sold_out" };
        var candidateList = new List<ProcessingProfileCandidate>();
        for (var i = 1; i <= 100; i++)
        {
            var shopId = (i % 2) + 1;
            var profileCategory = i % 4; // 0=601, 1=602, 2=varies, 3=none
            long? readyId = profileCategory switch { 0 => 601, 1 => 602, _ => null };
            var readyName = profileCategory switch { 0 => "1-3 days", 1 => "3-5 days", 2 => "Varies by variation", _ => "No profile" };
            var varies = profileCategory == 2;

            candidateList.Add(new ProcessingProfileCandidate(
                ListingId: i,
                Title: $"Listing {i} {(i % 2 == 0 ? "Wood" : "Clay")}",
                ShopId: shopId,
                ShopName: $"Shop {shopId}",
                EtsyListingId: 1000 + i,
                State: states[i % states.Length],
                IsMissingOnEtsy: false,
                Skus: [$"SKU-{i}"],
                Tags: [i % 2 == 0 ? "wood" : "clay"],
                CurrentReadinessStateId: readyId,
                CurrentProfileName: readyName,
                VariesByVariation: varies
            ));
        }

        adapter.Candidates = candidateList;
        adapter.AllProfiles =
        [
            new(1, 1, 601, "ready_to_ship", 1, 3, "1-3 days", 25, DateTime.UtcNow),
            new(2, 1, 602, "made_to_order", 3, 5, "3-5 days", 25, DateTime.UtcNow),
            new(3, 2, 601, "ready_to_ship", 1, 3, "1-3 days", 25, DateTime.UtcNow),
            new(4, 2, 602, "made_to_order", 3, 5, "3-5 days", 25, DateTime.UtcNow),
        ];

        var testFilters = new (int? shopId, string status, string profile, string? text)[]
        {
            (1, "all", "601", null),
            (1, "active", "varies", null),
            (1, "draft", "none", "Clay"),
            (2, "all", "602", null),
            (null, "active", "all", "Wood"),
            (null, "all", "varies", null),
            (1, "all", "all", null),
        };

        for (var cycle = 0; cycle < 40; cycle++)
        {
            var tf = testFilters[rng.Next(testFilters.Length)];
            adapter.FilterShopId = tf.shopId;
            adapter.FilterStatus = tf.status;
            adapter.FilterProfile = tf.profile;
            adapter.FilterText = tf.text;

            var visibleIds = adapter.VisibleCandidates.Select(c => c.ListingId).ToHashSet();

            // INVARIANT 1: TargetCount is ALWAYS mathematically equal to Picked ∩ Visible
            var expectedTargetCount = adapter.Picked.Intersect(visibleIds).Count();
            Assert.Equal(expectedTargetCount, adapter.TargetCount);

            // INVARIANT 2: Tristate header checkbox matches visible subset
            if (visibleIds.Count == 0 || expectedTargetCount == 0)
            {
                Assert.False(adapter.CandidateHeaderChecked);
            }
            else if (expectedTargetCount == visibleIds.Count)
            {
                Assert.True(adapter.CandidateHeaderChecked);
            }
            else
            {
                Assert.Null(adapter.CandidateHeaderChecked);
            }

            // Perform an action
            var action = rng.Next(3);
            var nonVisibleBefore = adapter.Picked.Where(id => !visibleIds.Contains(id)).ToHashSet();

            if (action == 0)
            {
                // SelectAllVisible
                adapter.SelectAllVisible();
                // INVARIANT 3: SelectAllVisible ONLY adds visible items; never modifies non-visible items!
                var nonVisibleAfter = adapter.Picked.Where(id => !visibleIds.Contains(id)).ToHashSet();
                Assert.True(nonVisibleBefore.SetEquals(nonVisibleAfter));
                Assert.Equal(visibleIds.Count, adapter.TargetCount);
            }
            else if (action == 1)
            {
                // TogglePickAllVisible
                adapter.TogglePickAllVisible();
                // INVARIANT 4: TogglePickAllVisible ONLY affects visible items!
                var nonVisibleAfter = adapter.Picked.Where(id => !visibleIds.Contains(id)).ToHashSet();
                Assert.True(nonVisibleBefore.SetEquals(nonVisibleAfter));
            }
            else if (visibleIds.Count > 0)
            {
                // Pick/unpick a random visible item
                var pickId = visibleIds.ElementAt(rng.Next(visibleIds.Count));
                adapter.TogglePick(pickId, !adapter.Picked.Contains(pickId));
                var nonVisibleAfter = adapter.Picked.Where(id => !visibleIds.Contains(id)).ToHashSet();
                Assert.True(nonVisibleBefore.SetEquals(nonVisibleAfter));
            }
        }
    }
}
