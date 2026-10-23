using System.Reflection;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Components.Pages.Listings;
using EtsyShopManager.Web.Components.Pages.Orders;
using EtsyShopManager.Web.Components.Pages.Templates;
using EtsyShopManager.Web.Components.Shared;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.AspNetCore.Components.Web;
using Xunit;

namespace EtsyShopManager.Tests;

/// <summary>
/// Adversarial integration tests verifying range selection across multi-select tables in:
/// - PendingChanges.razor (skipping unpushable/blocked items)
/// - Orders.razor (open vs history selection isolation)
/// - VariationListPage.razor (composite key (ListingId, PropertyId) under various sort orders)
/// - Listings.razor (bulk edit range selection under paging and filtering)
/// </summary>
public class RangeSelectionPageIntegrationTests
{
    #region Adapters

    private sealed class PendingChangesAdapter
    {
        public PendingChanges Page { get; } = new();

        public IReadOnlyList<PendingListing>? Rows
        {
            get => (IReadOnlyList<PendingListing>?)GetField("_rows");
            set => SetField("_rows", value);
        }

        public HashSet<int> Selected => (HashSet<int>)GetField("_selected")!;

        public RangeSelection<int> Range => (RangeSelection<int>)GetField("_range")!;

        public TableSort<PendingListing> Sort => (TableSort<PendingListing>)GetField("_sort")!;

        public IReadOnlyList<PendingListing> Ready => (IReadOnlyList<PendingListing>)GetProperty("Ready")!;

        public IReadOnlyList<PendingListing> SelectedReady => (IReadOnlyList<PendingListing>)GetProperty("SelectedReady")!;

        public bool AllSelected => (bool)GetProperty("AllSelected")!;

        public void HandleRowClick(int listingId, bool shiftKey) =>
            InvokeMethod("HandleRowClick", listingId, new MouseEventArgs { ShiftKey = shiftKey });

        public void SelectAll(bool selected) =>
            InvokeMethod("SelectAll", selected);

        public void ToggleAll() =>
            InvokeMethod("ToggleAll");

        private object? GetField(string name) =>
            typeof(PendingChanges).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private void SetField(string name, object? value) =>
            typeof(PendingChanges).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);

        private object? GetProperty(string name) =>
            typeof(PendingChanges).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private object? InvokeMethod(string name, params object?[] parameters) =>
            typeof(PendingChanges).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, parameters);
    }

    private sealed class OrdersAdapter
    {
        public Orders Page { get; } = new();

        public List<Order> OpenOrders
        {
            get => (List<Order>)GetField("_open")!;
            set
            {
                SetField("_open", value);
                SetField("_shown", value);
            }
        }

        public IReadOnlyList<Order> HistoryRows
        {
            get => (IReadOnlyList<Order>)GetField("_historyRows")!;
            set => SetField("_historyRows", value);
        }

        public HashSet<int> OpenPicked => (HashSet<int>)GetField("_picked")!;

        public HashSet<int> HistoryPicked => (HashSet<int>)GetField("_historyPicked")!;

        public RangeSelection<int> OpenRange => (RangeSelection<int>)GetField("_openRange")!;

        public RangeSelection<int> HistoryRange => (RangeSelection<int>)GetField("_historyRange")!;

        public void HandleOrderClick(int id, bool shiftKey, bool history) =>
            InvokeMethod("HandleOrderClick", id, new MouseEventArgs { ShiftKey = shiftKey }, history);

        public void ClearPicked() =>
            InvokeMethod("ClearPicked");

        public void ClearHistoryPicked() =>
            InvokeMethod("ClearHistoryPicked");

        public void SelectAllOpen() =>
            InvokeMethod("SelectAllOpen");

        public void PickHistoryPage(bool on) =>
            InvokeMethod("PickHistoryPage", on);

        private object? GetField(string name) =>
            typeof(Orders).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private void SetField(string name, object? value) =>
            typeof(Orders).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);

        private object? InvokeMethod(string name, params object?[] parameters) =>
            typeof(Orders).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, parameters);
    }

    private sealed class VariationListPageAdapter
    {
        public VariationListPage Page { get; } = new();

        public bool CanEdit
        {
            get => (bool)GetField("_canEdit")!;
            set => SetField("_canEdit", value);
        }

        public IReadOnlyList<VariationListCandidate> Candidates
        {
            get => (IReadOnlyList<VariationListCandidate>)GetField("_candidates")!;
            set => SetField("_candidates", value);
        }

        public HashSet<(int ListingId, long PropertyId)> Picked =>
            (HashSet<(int ListingId, long PropertyId)>)GetField("_picked")!;

        public RangeSelection<(int ListingId, long PropertyId)> Range =>
            (RangeSelection<(int ListingId, long PropertyId)>)GetField("_range")!;

        public TableSort<VariationListCandidate> CandidateSort =>
            (TableSort<VariationListCandidate>)GetField("_candidateSort")!;

        public IReadOnlyList<VariationListCandidate> VisibleCandidates =>
            (IReadOnlyList<VariationListCandidate>)GetProperty("VisibleCandidates")!;

        public bool? CandidateHeaderChecked =>
            (bool?)GetProperty("CandidateHeaderChecked");

        public void HandleCandidateClick((int ListingId, long PropertyId) key, bool shiftKey) =>
            InvokeMethod("HandleCandidateClick", key, new MouseEventArgs { ShiftKey = shiftKey });

        public void TogglePickAllVisible() =>
            InvokeMethod("TogglePickAllVisible");

        private object? GetField(string name) =>
            typeof(VariationListPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private void SetField(string name, object? value) =>
            typeof(VariationListPage).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);

        private object? GetProperty(string name) =>
            typeof(VariationListPage).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private object? InvokeMethod(string name, params object?[] parameters) =>
            typeof(VariationListPage).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, parameters);
    }

    private sealed class ListingsAdapter
    {
        public Listings Page { get; } = new();

        public bool Selecting
        {
            get => (bool)GetField("_selecting")!;
            set => SetField("_selecting", value);
        }

        public List<ListingRow>? Rows
        {
            get => (List<ListingRow>?)GetField("_rows");
            set => SetField("_rows", value);
        }

        public HashSet<int> Selected => (HashSet<int>)GetField("_selected")!;

        public RangeSelection<int> Range => (RangeSelection<int>)GetField("_range")!;

        public int PageNumber
        {
            get => (int)GetField("_page")!;
            set => SetField("_page", value);
        }

        public bool PageSelected => (bool)GetProperty("PageSelected")!;

        public void HandleRowClick(int id, bool shiftKey) =>
            InvokeMethod("HandleRowClick", id, new MouseEventArgs { ShiftKey = shiftKey });

        public void ToggleSelecting() =>
            InvokeMethod("ToggleSelecting");

        public void SelectPage(bool selected) =>
            InvokeMethod("SelectPage", selected);

        public void ClearSelection() =>
            InvokeMethod("ClearSelection");

        private object? GetField(string name) =>
            typeof(Listings).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private void SetField(string name, object? value) =>
            typeof(Listings).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(Page, value);

        private object? GetProperty(string name) =>
            typeof(Listings).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Page);

        private object? InvokeMethod(string name, params object?[] parameters) =>
            typeof(Listings).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, parameters);
    }

    #endregion

    #region Helper Factories

    private static PendingListing CreatePendingListing(int id, string title, bool canPush)
    {
        var changes = new List<PendingFieldChange> { new("Title", "Original Title", title) };
        var blockers = canPush ? [] : new[] { "Changed on Etsy: resolve conflict first" };
        var previewChanges = new List<FieldChange> { new("title", "Title", null, null) };
        var preview = new PushPreview(previewChanges, blockers, []);
        return new PendingListing(
            ListingId: id,
            Title: title,
            ShopId: 1,
            ShopName: "Shop 1",
            EtsyListingId: id * 1000L,
            ThumbFileName: null,
            EditedAtUtc: DateTime.UtcNow,
            EditedBy: "tophattwaffle",
            Changes: changes,
            Preview: preview,
            Reviewed: $"rev-{id}"
        );
    }

    private static Order CreateOrder(int id, long receiptId, bool isOpen = true)
    {
        return new Order
        {
            Id = id,
            ShopId = 1,
            EtsyReceiptId = receiptId,
            BuyerUserId = 1000 + id,
            BuyerName = $"Buyer {id}",
            Status = isOpen ? "open" : "completed",
            IsPaid = true,
            IsShipped = !isOpen,
            FirstSeenAtUtc = DateTime.UtcNow,
            LastSyncedAtUtc = DateTime.UtcNow,
            Items = []
        };
    }

    private static VariationListCandidate CreateCandidate(
        int listingId,
        long propertyId,
        string title,
        string variationName,
        int inList = 3,
        int values = 3)
    {
        return new VariationListCandidate(
            ListingId: listingId,
            Title: title,
            ShopId: 1,
            ShopName: "Shop 1",
            EtsyListingId: listingId * 1000L,
            State: "active",
            IsMissingOnEtsy: false,
            Skus: [],
            Tags: [],
            PropertyId: propertyId,
            VariationName: variationName,
            InList: inList,
            Values: values
        );
    }

    private static ListingRow CreateListingRow(int id, string title, string state = "active", int quantity = 10)
    {
        return new ListingRow(
            Id: id,
            ShopId: 1,
            ShopName: "Shop 1",
            EtsyListingId: id * 1000L,
            Title: title,
            State: state,
            PriceAmount: 2000,
            PriceDivisor: 100,
            CurrencyCode: "USD",
            Quantity: quantity,
            Skus: [$"SKU-{id}"],
            ThumbFileName: null,
            ImageCount: 1,
            VariationCount: 0,
            EtsyUpdatedAtUtc: DateTime.UtcNow,
            LastImportedAtUtc: DateTime.UtcNow,
            IsMissingOnEtsy: false,
            Refill: null
        );
    }

    #endregion

    #region PendingChanges Tests (Blocked Item Interspersion)

    [Fact]
    public void PendingChanges_ForwardRangeSelection_WithInterspersedBlockedItems_SelectsOnlyPushableItems()
    {
        var adapter = new PendingChangesAdapter();
        // 5 items: 101 (ready), 102 (blocked), 103 (ready), 104 (blocked), 105 (ready)
        adapter.Rows =
        [
            CreatePendingListing(101, "Item 101", canPush: true),
            CreatePendingListing(102, "Item 102", canPush: false),
            CreatePendingListing(103, "Item 103", canPush: true),
            CreatePendingListing(104, "Item 104", canPush: false),
            CreatePendingListing(105, "Item 105", canPush: true),
        ];

        // Click 101 without shift
        adapter.HandleRowClick(101, shiftKey: false);
        Assert.Equal([101], adapter.Selected.Order());
        Assert.Equal(101, adapter.Range.AnchorKey);

        // Shift-click 105
        adapter.HandleRowClick(105, shiftKey: true);

        // Blocked items 102 and 104 MUST NOT be selected; ONLY ready items 101, 103, 105 are selected
        Assert.Equal([101, 103, 105], adapter.Selected.Order());
        Assert.DoesNotContain(102, adapter.Selected);
        Assert.DoesNotContain(104, adapter.Selected);

        // SelectedReady matches exactly
        Assert.Equal([101, 103, 105], adapter.SelectedReady.Select(r => r.ListingId).Order());
    }

    [Fact]
    public void PendingChanges_BackwardRangeSelection_WithInterspersedBlockedItems_SelectsOnlyPushableItems()
    {
        var adapter = new PendingChangesAdapter();
        adapter.Rows =
        [
            CreatePendingListing(101, "Item 101", canPush: true),
            CreatePendingListing(102, "Item 102", canPush: false),
            CreatePendingListing(103, "Item 103", canPush: true),
            CreatePendingListing(104, "Item 104", canPush: false),
            CreatePendingListing(105, "Item 105", canPush: true),
        ];

        // Click bottom item 105 without shift
        adapter.HandleRowClick(105, shiftKey: false);
        Assert.Equal([105], adapter.Selected.Order());

        // Shift-click top item 101 (backward range)
        adapter.HandleRowClick(101, shiftKey: true);

        // Verify ONLY pushable items are selected
        Assert.Equal([101, 103, 105], adapter.Selected.Order());
        Assert.DoesNotContain(102, adapter.Selected);
        Assert.DoesNotContain(104, adapter.Selected);
        Assert.Equal(105, adapter.Range.AnchorKey); // Anchor remains at 105
    }

    [Fact]
    public void PendingChanges_ClickingDirectlyOnBlockedItem_IsIgnoredAndDoesNotSetAnchor()
    {
        var adapter = new PendingChangesAdapter();
        adapter.Rows =
        [
            CreatePendingListing(101, "Item 101", canPush: true),
            CreatePendingListing(102, "Item 102", canPush: false),
            CreatePendingListing(103, "Item 103", canPush: true),
        ];

        // Click directly on blocked item 102
        adapter.HandleRowClick(102, shiftKey: false);

        // Item 102 cannot be selected, anchor must not be set
        Assert.Empty(adapter.Selected);
        Assert.False(adapter.Range.HasAnchor);
        Assert.Equal(0, adapter.Range.AnchorKey);
    }

    [Fact]
    public void PendingChanges_ShiftClickingDirectlyOnBlockedItem_SelectsPushableItemsInRange_AndLeavesBlockedItemUnselected()
    {
        var adapter = new PendingChangesAdapter();
        adapter.Rows =
        [
            CreatePendingListing(101, "Item 101", canPush: true),
            CreatePendingListing(102, "Item 102", canPush: false),
            CreatePendingListing(103, "Item 103", canPush: true),
            CreatePendingListing(104, "Item 104", canPush: false),
            CreatePendingListing(105, "Item 105", canPush: true),
        ];

        // Anchor at 101
        adapter.HandleRowClick(101, shiftKey: false);
        Assert.Equal([101], adapter.Selected.Order());

        // Shift-click on blocked item 104
        adapter.HandleRowClick(104, shiftKey: true);

        // Range [101..104] evaluated: 101 selected, 102 skipped (blocked), 103 selected, 104 skipped (blocked)
        Assert.Equal([101, 103], adapter.Selected.Order());
        Assert.DoesNotContain(104, adapter.Selected);
        Assert.DoesNotContain(102, adapter.Selected);
    }

    [Fact]
    public void PendingChanges_RangeDeselection_WithInterspersedBlockedItems_UnselectsOnlyPushableItems()
    {
        var adapter = new PendingChangesAdapter();
        adapter.Rows =
        [
            CreatePendingListing(101, "Item 101", canPush: true),
            CreatePendingListing(102, "Item 102", canPush: false),
            CreatePendingListing(103, "Item 103", canPush: true),
            CreatePendingListing(104, "Item 104", canPush: false),
            CreatePendingListing(105, "Item 105", canPush: true),
        ];

        // Pre-select all pushable items
        adapter.Selected.UnionWith([101, 103, 105]);

        // Normal click on 101 unchecks it (AnchorState becomes false)
        adapter.HandleRowClick(101, shiftKey: false);
        Assert.DoesNotContain(101, adapter.Selected);
        Assert.False(adapter.Range.AnchorState);

        // Shift-click 105 deselects range [101..105]
        adapter.HandleRowClick(105, shiftKey: true);

        // All pushable items unselected; blocked items unaffected
        Assert.Empty(adapter.Selected);
        Assert.Empty(adapter.SelectedReady);
    }

    [Fact]
    public void PendingChanges_SelectAll_ResetsAnchor_SoNextShiftClickActsAsNormalClick()
    {
        var adapter = new PendingChangesAdapter();
        adapter.Rows =
        [
            CreatePendingListing(101, "Item 101", canPush: true),
            CreatePendingListing(102, "Item 102", canPush: false),
            CreatePendingListing(103, "Item 103", canPush: true),
        ];

        adapter.SelectAll(true);
        Assert.Equal([101, 103], adapter.Selected.Order());
        Assert.False(adapter.Range.HasAnchor);

        // Shift-click 101 with no anchor treats it as a single click (unchecks 101)
        adapter.HandleRowClick(101, shiftKey: true);
        Assert.Equal([103], adapter.Selected.Order());
        Assert.Equal(101, adapter.Range.AnchorKey);
    }

    #endregion

    #region Orders Tests (Open vs History Isolation)

    [Fact]
    public void Orders_OpenOrderClick_SetsOpenAnchor_WithoutInterferingWithHistoryRangeOrSelection()
    {
        var adapter = new OrdersAdapter();
        adapter.OpenOrders =
        [
            CreateOrder(1, 1001, isOpen: true),
            CreateOrder(2, 1002, isOpen: true),
            CreateOrder(3, 1003, isOpen: true),
            CreateOrder(4, 1004, isOpen: true),
        ];
        adapter.HistoryRows =
        [
            CreateOrder(11, 2001, isOpen: false),
            CreateOrder(12, 2002, isOpen: false),
            CreateOrder(13, 2003, isOpen: false),
            CreateOrder(14, 2004, isOpen: false),
        ];

        // Click Open order 2
        adapter.HandleOrderClick(2, shiftKey: false, history: false);

        Assert.Equal([2], adapter.OpenPicked.Order());
        Assert.True(adapter.OpenRange.HasAnchor);
        Assert.Equal(2, adapter.OpenRange.AnchorKey);

        // Invariant: History range and selection remain completely untouched
        Assert.Empty(adapter.HistoryPicked);
        Assert.False(adapter.HistoryRange.HasAnchor);
        Assert.Equal(0, adapter.HistoryRange.AnchorKey);
    }

    [Fact]
    public void Orders_HistoryOrderClick_SetsHistoryAnchor_WithoutInterferingWithOpenRangeOrSelection()
    {
        var adapter = new OrdersAdapter();
        adapter.OpenOrders =
        [
            CreateOrder(1, 1001, isOpen: true),
            CreateOrder(2, 1002, isOpen: true),
        ];
        adapter.HistoryRows =
        [
            CreateOrder(11, 2001, isOpen: false),
            CreateOrder(12, 2002, isOpen: false),
            CreateOrder(13, 2003, isOpen: false),
        ];

        // Click History order 12
        adapter.HandleOrderClick(12, shiftKey: false, history: true);

        Assert.Equal([12], adapter.HistoryPicked.Order());
        Assert.True(adapter.HistoryRange.HasAnchor);
        Assert.Equal(12, adapter.HistoryRange.AnchorKey);

        // Invariant: Open range and selection remain untouched
        Assert.Empty(adapter.OpenPicked);
        Assert.False(adapter.OpenRange.HasAnchor);
        Assert.Equal(0, adapter.OpenRange.AnchorKey);
    }

    [Fact]
    public void Orders_ShiftClickAcrossTabs_FallsBackGracefully_DueToIsolatedAnchors()
    {
        var adapter = new OrdersAdapter();
        adapter.OpenOrders =
        [
            CreateOrder(1, 1001, isOpen: true),
            CreateOrder(2, 1002, isOpen: true),
            CreateOrder(3, 1003, isOpen: true),
        ];
        adapter.HistoryRows =
        [
            CreateOrder(11, 2001, isOpen: false),
            CreateOrder(12, 2002, isOpen: false),
            CreateOrder(13, 2003, isOpen: false),
        ];

        // 1. User clicks Open order 1
        adapter.HandleOrderClick(1, shiftKey: false, history: false);
        Assert.Equal([1], adapter.OpenPicked.Order());
        Assert.Equal(1, adapter.OpenRange.AnchorKey);

        // 2. User switches to History tab and Shift-clicks History order 13
        // Since HistoryRange has NO anchor, it must NOT use Open order 1!
        // It must fall back to a single click on 13.
        adapter.HandleOrderClick(13, shiftKey: true, history: true);

        Assert.Equal([13], adapter.HistoryPicked.Order());
        Assert.Equal(13, adapter.HistoryRange.AnchorKey);
        // Open tab state remains pristine
        Assert.Equal([1], adapter.OpenPicked.Order());
        Assert.Equal(1, adapter.OpenRange.AnchorKey);

        // 3. User switches back to Open tab and Shift-clicks Open order 3
        // Uses Open anchor 1!
        adapter.HandleOrderClick(3, shiftKey: true, history: false);

        Assert.Equal([1, 2, 3], adapter.OpenPicked.Order());
        Assert.Equal(1, adapter.OpenRange.AnchorKey);
        // History tab remains untouched
        Assert.Equal([13], adapter.HistoryPicked.Order());
        Assert.Equal(13, adapter.HistoryRange.AnchorKey);
    }

    [Fact]
    public void Orders_IdenticalIdsAcrossOpenAndHistory_StayCompletelyIsolated()
    {
        var adapter = new OrdersAdapter();
        // ID 50 exists in both tables
        adapter.OpenOrders =
        [
            CreateOrder(50, 1050, isOpen: true),
            CreateOrder(51, 1051, isOpen: true),
            CreateOrder(52, 1052, isOpen: true),
        ];
        adapter.HistoryRows =
        [
            CreateOrder(50, 2050, isOpen: false),
            CreateOrder(51, 2051, isOpen: false),
            CreateOrder(52, 2052, isOpen: false),
        ];

        // Click Open 50
        adapter.HandleOrderClick(50, shiftKey: false, history: false);
        // Click History 50
        adapter.HandleOrderClick(50, shiftKey: false, history: true);

        Assert.Equal([50], adapter.OpenPicked.Order());
        Assert.Equal([50], adapter.HistoryPicked.Order());

        // Shift-click Open 52 -> selects [50, 51, 52] in Open
        adapter.HandleOrderClick(52, shiftKey: true, history: false);
        Assert.Equal([50, 51, 52], adapter.OpenPicked.Order());
        // History picked is still only [50]
        Assert.Equal([50], adapter.HistoryPicked.Order());

        // Clear Open selection
        adapter.ClearPicked();
        Assert.Empty(adapter.OpenPicked);
        Assert.False(adapter.OpenRange.HasAnchor);

        // History anchor at 50 is still active!
        Assert.True(adapter.HistoryRange.HasAnchor);
        Assert.Equal(50, adapter.HistoryRange.AnchorKey);

        // Shift-click History 52 -> selects [50, 51, 52] in History
        adapter.HandleOrderClick(52, shiftKey: true, history: true);
        Assert.Equal([50, 51, 52], adapter.HistoryPicked.Order());
        Assert.Empty(adapter.OpenPicked);
    }

    [Fact]
    public void Orders_SelectAllAndClear_IsolatesRanges()
    {
        var adapter = new OrdersAdapter();
        adapter.OpenOrders = [CreateOrder(1, 1001), CreateOrder(2, 1002)];
        adapter.HistoryRows = [CreateOrder(11, 2001), CreateOrder(12, 2002)];

        adapter.HandleOrderClick(1, shiftKey: false, history: false);
        adapter.HandleOrderClick(11, shiftKey: false, history: true);

        Assert.True(adapter.OpenRange.HasAnchor);
        Assert.True(adapter.HistoryRange.HasAnchor);

        // SelectAllOpen resets OpenRange, leaves HistoryRange intact
        adapter.SelectAllOpen();
        Assert.False(adapter.OpenRange.HasAnchor);
        Assert.True(adapter.HistoryRange.HasAnchor);

        // PickHistoryPage resets HistoryRange, leaves OpenRange intact
        adapter.PickHistoryPage(true);
        Assert.False(adapter.HistoryRange.HasAnchor);
    }

    #endregion

    #region VariationListPage Tests (Composite Key & Sorting)

    [Fact]
    public void VariationListPage_CompositeKey_RangeSelection_FollowsTitleAscendingSort()
    {
        var adapter = new VariationListPageAdapter { CanEdit = true };
        // 4 candidates with titles in non-sorted order:
        // C1: Listing 30, Property 100, Title "Delta"
        // C2: Listing 10, Property 100, Title "Alpha"
        // C3: Listing 40, Property 200, Title "Gamma"
        // C4: Listing 20, Property 100, Title "Beta"
        adapter.Candidates =
        [
            CreateCandidate(30, 100L, "Delta", "Color"),
            CreateCandidate(10, 100L, "Alpha", "Color"),
            CreateCandidate(40, 200L, "Gamma", "Size"),
            CreateCandidate(20, 100L, "Beta", "Color"),
        ];

        // Sort by Title ("listing") ascending -> Order is: Alpha(10,100), Beta(20,100), Delta(30,100), Gamma(40,200)
        adapter.CandidateSort.Set("listing", descending: false);

        // Click "Beta" (20, 100)
        adapter.HandleCandidateClick((20, 100L), shiftKey: false);
        Assert.Equal([(20, 100L)], adapter.Picked);

        // Shift-click "Gamma" (40, 200)
        adapter.HandleCandidateClick((40, 200L), shiftKey: true);

        // In sorted order: Beta -> Delta -> Gamma.
        // Therefore, Delta (30, 100) MUST be included, but Alpha (10, 100) MUST NOT be included!
        Assert.Contains((20, 100L), adapter.Picked);
        Assert.Contains((30, 100L), adapter.Picked);
        Assert.Contains((40, 200L), adapter.Picked);
        Assert.DoesNotContain((10, 100L), adapter.Picked);
        Assert.Equal(3, adapter.Picked.Count);
    }

    [Fact]
    public void VariationListPage_CompositeKey_RangeSelection_FollowsTitleDescendingSort()
    {
        var adapter = new VariationListPageAdapter { CanEdit = true };
        adapter.Candidates =
        [
            CreateCandidate(10, 100L, "Alpha", "Color"),
            CreateCandidate(20, 100L, "Beta", "Color"),
            CreateCandidate(30, 100L, "Delta", "Color"),
            CreateCandidate(40, 200L, "Gamma", "Size"),
        ];

        // Sort by Title descending -> Order is: Gamma(40,200), Delta(30,100), Beta(20,100), Alpha(10,100)
        adapter.CandidateSort.Set("listing", descending: true);

        // Click "Gamma" (40, 200)
        adapter.HandleCandidateClick((40, 200L), shiftKey: false);

        // Shift-click "Beta" (20, 100)
        adapter.HandleCandidateClick((20, 100L), shiftKey: true);

        // Range in reverse sort: Gamma -> Delta -> Beta.
        // Delta (30, 100) is included; Alpha (10, 100) is NOT included!
        Assert.Contains((40, 200L), adapter.Picked);
        Assert.Contains((30, 100L), adapter.Picked);
        Assert.Contains((20, 100L), adapter.Picked);
        Assert.DoesNotContain((10, 100L), adapter.Picked);
        Assert.Equal(3, adapter.Picked.Count);
    }

    [Fact]
    public void VariationListPage_CompositeKey_RangeSelection_FollowsVariationNameSort()
    {
        var adapter = new VariationListPageAdapter { CanEdit = true };
        adapter.Candidates =
        [
            CreateCandidate(1, 100L, "Item 1", "Size"),
            CreateCandidate(2, 200L, "Item 2", "Color"),
            CreateCandidate(3, 300L, "Item 3", "Material"),
        ];

        // Sort by variation ascending -> Color(2,200), Material(3,300), Size(1,100)
        adapter.CandidateSort.Set("variation", descending: false);

        // Click "Color" (2, 200)
        adapter.HandleCandidateClick((2, 200L), shiftKey: false);

        // Shift-click "Material" (3, 300)
        adapter.HandleCandidateClick((3, 300L), shiftKey: true);

        Assert.Equal([(2, 200L), (3, 300L)], adapter.Picked.OrderBy(k => k.ListingId));
        Assert.DoesNotContain((1, 100L), adapter.Picked);
    }

    [Fact]
    public void VariationListPage_CompositeKey_RangeSelection_WithSameListingMultipleVariations()
    {
        var adapter = new VariationListPageAdapter { CanEdit = true };
        // Listing 10 has TWO distinct variation candidates: Property 101 and Property 102
        adapter.Candidates =
        [
            CreateCandidate(10, 101L, "Listing 10", "Color"),
            CreateCandidate(10, 102L, "Listing 10", "Size"),
            CreateCandidate(20, 101L, "Listing 20", "Color"),
        ];

        adapter.CandidateSort.Set("listing", descending: false);

        // Click Listing 10 Color
        adapter.HandleCandidateClick((10, 101L), shiftKey: false);
        // Shift-click Listing 10 Size
        adapter.HandleCandidateClick((10, 102L), shiftKey: true);

        // Selects both variations for Listing 10; Listing 20 unselected
        Assert.Contains((10, 101L), adapter.Picked);
        Assert.Contains((10, 102L), adapter.Picked);
        Assert.DoesNotContain((20, 101L), adapter.Picked);
        Assert.Equal(2, adapter.Picked.Count);

        // Extend range to Listing 20 Color
        adapter.HandleCandidateClick((20, 101L), shiftKey: true);
        Assert.Equal(3, adapter.Picked.Count);
    }

    [Fact]
    public void VariationListPage_WhenCannotEdit_IgnoresNormalAndShiftClicks()
    {
        var adapter = new VariationListPageAdapter { CanEdit = false };
        adapter.Candidates = [CreateCandidate(1, 100L, "Item 1", "Color")];

        adapter.HandleCandidateClick((1, 100L), shiftKey: false);
        Assert.Empty(adapter.Picked);
        Assert.False(adapter.Range.HasAnchor);

        adapter.HandleCandidateClick((1, 100L), shiftKey: true);
        Assert.Empty(adapter.Picked);
    }

    [Fact]
    public void VariationListPage_TogglePickAllVisible_ResetsRangeAnchor()
    {
        var adapter = new VariationListPageAdapter { CanEdit = true };
        adapter.Candidates =
        [
            CreateCandidate(1, 100L, "Item 1", "Color"),
            CreateCandidate(2, 200L, "Item 2", "Size"),
        ];

        adapter.HandleCandidateClick((1, 100L), shiftKey: false);
        Assert.True(adapter.Range.HasAnchor);

        adapter.TogglePickAllVisible();
        Assert.False(adapter.Range.HasAnchor);
    }

    #endregion

    #region Listings Tests (Paging, Filtering & Bulk Selection)

    [Fact]
    public void Listings_RangeSelection_InBulkEditMode_SelectsContiguousRange()
    {
        var adapter = new ListingsAdapter { Selecting = true };
        adapter.Rows =
        [
            CreateListingRow(1, "Listing 1"),
            CreateListingRow(2, "Listing 2"),
            CreateListingRow(3, "Listing 3"),
            CreateListingRow(4, "Listing 4"),
            CreateListingRow(5, "Listing 5"),
        ];

        adapter.HandleRowClick(2, shiftKey: false);
        Assert.Equal([2], adapter.Selected.Order());
        Assert.Equal(2, adapter.Range.AnchorKey);

        adapter.HandleRowClick(4, shiftKey: true);
        Assert.Equal([2, 3, 4], adapter.Selected.Order());
        Assert.DoesNotContain(1, adapter.Selected);
        Assert.DoesNotContain(5, adapter.Selected);
    }

    [Fact]
    public void Listings_PagingTransition_WhenAnchorOffPage_FallsBackToNormalClickWithoutError()
    {
        var adapter = new ListingsAdapter { Selecting = true };
        // Page 0 has listings 1..5
        adapter.PageNumber = 0;
        adapter.Rows =
        [
            CreateListingRow(1, "Listing 1"),
            CreateListingRow(2, "Listing 2"),
            CreateListingRow(3, "Listing 3"),
        ];

        // Anchor set on Page 0 at listing 2
        adapter.HandleRowClick(2, shiftKey: false);
        Assert.Equal([2], adapter.Selected.Order());
        Assert.Equal(2, adapter.Range.AnchorKey);

        // User navigates to Page 1: rows are replaced with listings 11..13
        adapter.PageNumber = 1;
        adapter.Rows =
        [
            CreateListingRow(11, "Listing 11"),
            CreateListingRow(12, "Listing 12"),
            CreateListingRow(13, "Listing 13"),
        ];

        // Shift-click listing 12 on Page 1:
        // Anchor (2) is not on this page. Range selection must NOT throw and must gracefully
        // fall back to a normal click on listing 12, setting anchor to 12.
        adapter.HandleRowClick(12, shiftKey: true);

        Assert.Equal([2, 12], adapter.Selected.Order());
        Assert.Equal(12, adapter.Range.AnchorKey);

        // Subsequent Shift-click on Page 1 extends from anchor 12 to 13
        adapter.HandleRowClick(13, shiftKey: true);
        Assert.Equal([2, 12, 13], adapter.Selected.Order());
        Assert.DoesNotContain(11, adapter.Selected);
    }

    [Fact]
    public void Listings_FilteringTransition_RangeSelectionRespectsFilteredVisibleRows()
    {
        var adapter = new ListingsAdapter { Selecting = true };
        // Filter "Vase" produces 3 matching visible rows (IDs 10, 30, 50)
        adapter.Rows =
        [
            CreateListingRow(10, "Galaxy Vase"),
            CreateListingRow(30, "Moon Vase"),
            CreateListingRow(50, "Star Vase"),
        ];

        // Click row 10
        adapter.HandleRowClick(10, shiftKey: false);
        Assert.Equal([10], adapter.Selected.Order());

        // Shift-click row 50
        adapter.HandleRowClick(50, shiftKey: true);

        // Only the visible filtered rows [10, 30, 50] are selected
        Assert.Equal([10, 30, 50], adapter.Selected.Order());
    }

    [Fact]
    public void Listings_ToggleSelecting_ResetsRangeAnchor_PreventingStaleAnchorsWhenReEntering()
    {
        var adapter = new ListingsAdapter { Selecting = true };
        adapter.Rows = [CreateListingRow(1, "Listing 1"), CreateListingRow(2, "Listing 2")];

        adapter.HandleRowClick(1, shiftKey: false);
        Assert.True(adapter.Range.HasAnchor);

        // User clicks "Done" (exits selecting mode)
        adapter.ToggleSelecting();
        Assert.False(adapter.Selecting);
        Assert.False(adapter.Range.HasAnchor);

        // User clicks "Bulk edit" (re-enters selecting mode)
        adapter.ToggleSelecting();
        Assert.True(adapter.Selecting);
        Assert.False(adapter.Range.HasAnchor);

        // Shift-click behaves as first click (sets anchor, does not use previous anchor)
        adapter.HandleRowClick(2, shiftKey: true);
        Assert.Equal(2, adapter.Range.AnchorKey);
    }

    [Fact]
    public void Listings_SelectPage_And_ClearSelection_ResetRangeAnchor()
    {
        var adapter = new ListingsAdapter { Selecting = true };
        adapter.Rows = [CreateListingRow(1, "Listing 1"), CreateListingRow(2, "Listing 2")];

        adapter.HandleRowClick(1, shiftKey: false);
        Assert.True(adapter.Range.HasAnchor);

        // SelectPage resets anchor
        adapter.SelectPage(true);
        Assert.False(adapter.Range.HasAnchor);

        adapter.HandleRowClick(1, shiftKey: false);
        Assert.True(adapter.Range.HasAnchor);

        // ClearSelection resets anchor
        adapter.ClearSelection();
        Assert.Empty(adapter.Selected);
        Assert.False(adapter.Range.HasAnchor);
    }

    #endregion
}
