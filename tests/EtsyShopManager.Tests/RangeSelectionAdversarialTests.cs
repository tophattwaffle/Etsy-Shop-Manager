using System.Diagnostics;
using EtsyShopManager.Web.Components.Shared;
using Xunit;

namespace EtsyShopManager.Tests;

public class RangeSelectionAdversarialTests
{
    // =========================================================================
    // 1. Large List Stress Testing
    // =========================================================================

    [Fact]
    public void Large_list_10000_items_rapid_shift_clicks_completes_swiftly_and_maintains_invariants()
    {
        const int itemCount = 10_000;
        var visibleKeys = Enumerable.Range(0, itemCount).ToList();
        var selected = new HashSet<int>();
        var range = new RangeSelection<int>();

        // Set anchor at 5,000
        range.HandleClick(5000, shiftKey: false, visibleKeys, selected);
        Assert.True(range.HasAnchor);
        Assert.Equal(5000, range.AnchorKey);
        Assert.True(range.AnchorState);
        Assert.Contains(5000, selected);

        var stopwatch = Stopwatch.StartNew();

        // Perform 500 rapid Shift+Clicks to varying positions across the entire 10,000-item range
        var random = new Random(42);
        for (int i = 0; i < 500; i++)
        {
            int targetIndex = random.Next(0, itemCount);
            range.HandleClick(targetIndex, shiftKey: true, visibleKeys, selected);

            // Anchor must remain strictly fixed at 5,000
            Assert.True(range.HasAnchor);
            Assert.Equal(5000, range.AnchorKey);
            Assert.True(range.AnchorState);
        }

        stopwatch.Stop();

        // 500 shift-clicks across 10k items must easily complete within 2 seconds
        Assert.True(stopwatch.ElapsedMilliseconds < 2000, $"Expected < 2000ms, took {stopwatch.ElapsedMilliseconds}ms");
        Assert.Contains(5000, selected);
        Assert.NotEmpty(selected);
    }

    [Fact]
    public void Large_list_interleaved_select_and_deselect_sweeps()
    {
        const int itemCount = 2_000;
        var visibleKeys = Enumerable.Range(0, itemCount).ToList();
        var selected = new HashSet<int>();
        var range = new RangeSelection<int>();

        // Select range 500..1500
        range.HandleClick(500, shiftKey: false, visibleKeys, selected);
        range.HandleClick(1500, shiftKey: true, visibleKeys, selected);
        Assert.Equal(1001, selected.Count);

        // Deselect sub-range 700..900
        // Normal click on 700 deselects it
        range.HandleClick(700, shiftKey: false, visibleKeys, selected);
        Assert.DoesNotContain(700, selected);
        Assert.False(range.AnchorState);

        // Shift-click on 900 deselects 700..900
        range.HandleClick(900, shiftKey: true, visibleKeys, selected);
        for (int i = 700; i <= 900; i++)
        {
            Assert.DoesNotContain(i, selected);
        }
        // Boundaries outside 700..900 remain selected
        Assert.Contains(699, selected);
        Assert.Contains(901, selected);
    }

    // =========================================================================
    // 2. Rapid Alternating Shift+Clicks (Expanding, Contracting, Jumping)
    // =========================================================================

    [Fact]
    public void Expanding_range_from_fixed_anchor()
    {
        var range = new RangeSelection<int>();
        var visible = Enumerable.Range(0, 100).ToList();
        var selected = new HashSet<int>();

        // Set anchor at 50
        range.HandleClick(50, shiftKey: false, visible, selected);
        Assert.Equal(50, range.AnchorKey);

        // Step 1: Expand to 60
        range.HandleClick(60, shiftKey: true, visible, selected);
        Assert.Equal(11, selected.Count); // 50..60
        for (int i = 50; i <= 60; i++) Assert.Contains(i, selected);

        // Step 2: Expand to 75
        range.HandleClick(75, shiftKey: true, visible, selected);
        Assert.Equal(26, selected.Count); // 50..75
        for (int i = 50; i <= 75; i++) Assert.Contains(i, selected);

        // Step 3: Expand to 99
        range.HandleClick(99, shiftKey: true, visible, selected);
        Assert.Equal(50, selected.Count); // 50..99
        for (int i = 50; i <= 99; i++) Assert.Contains(i, selected);

        Assert.Equal(50, range.AnchorKey);
        Assert.True(range.AnchorState);
    }

    [Fact]
    public void Jumping_across_anchor_bidirectional_accumulates_selection()
    {
        var range = new RangeSelection<int>();
        var visible = Enumerable.Range(0, 100).ToList();
        var selected = new HashSet<int>();

        // Anchor at 50
        range.HandleClick(50, shiftKey: false, visible, selected);

        // Jump forward to 70 -> selects 50..70
        range.HandleClick(70, shiftKey: true, visible, selected);
        Assert.Equal(21, selected.Count);

        // Jump backward across anchor to 30 -> selects 30..50
        range.HandleClick(30, shiftKey: true, visible, selected);
        // Now 30..70 should all be in selected (since AnchorState is true and set accumulates)
        Assert.Equal(41, selected.Count);
        for (int i = 30; i <= 70; i++)
        {
            Assert.Contains(i, selected);
        }

        // Jump back to anchor itself (50)
        range.HandleClick(50, shiftKey: true, visible, selected);
        Assert.Equal(50, range.AnchorKey);
        Assert.Contains(50, selected);
    }

    [Fact]
    public void Deselection_jumping_across_anchor_removes_both_directions()
    {
        var range = new RangeSelection<int>();
        var visible = Enumerable.Range(0, 100).ToList();
        var selected = new HashSet<int>(visible); // Initially all 0..99 selected

        // Normal click at 50 deselects it and sets AnchorState = false
        range.HandleClick(50, shiftKey: false, visible, selected);
        Assert.DoesNotContain(50, selected);
        Assert.False(range.AnchorState);

        // Shift-click forward to 65 -> removes 50..65
        range.HandleClick(65, shiftKey: true, visible, selected);
        for (int i = 50; i <= 65; i++) Assert.DoesNotContain(i, selected);

        // Shift-click backward across anchor to 35 -> removes 35..50
        range.HandleClick(35, shiftKey: true, visible, selected);
        for (int i = 35; i <= 65; i++) Assert.DoesNotContain(i, selected);

        // Outside remains selected
        Assert.Contains(34, selected);
        Assert.Contains(66, selected);
        Assert.Equal(100 - (65 - 35 + 1), selected.Count);
    }

    // =========================================================================
    // 3. Invalidation Edge Cases
    // =========================================================================

    [Fact]
    public void Anchor_filtered_out_or_deleted_between_clicks_falls_back_to_normal_click()
    {
        var range = new RangeSelection<int>();
        var visible1 = new List<int> { 10, 20, 30, 40, 50 };
        var selected = new HashSet<int>();

        // Click 30 -> anchor is 30
        range.HandleClick(30, shiftKey: false, visible1, selected);
        Assert.Equal(30, range.AnchorKey);
        Assert.True(range.AnchorState);
        Assert.Contains(30, selected);

        // Now filter changes: item 30 is deleted or filtered out
        var visible2 = new List<int> { 10, 20, 40, 50 };

        // User Shift+Clicks 50. Since anchor 30 is not in visible2, fallback to normal click on 50
        range.HandleClick(50, shiftKey: true, visible2, selected);

        // 50 should be toggled (added to selected), and 50 becomes the NEW anchor!
        Assert.Contains(50, selected);
        Assert.Equal(50, range.AnchorKey);
        Assert.True(range.AnchorState);
        // Item 40 was NOT in range because fallback to normal click occurred
        Assert.DoesNotContain(40, selected);
        Assert.Contains(30, selected); // 30 is still in selected from previous click
    }

    [Fact]
    public void Clicked_item_not_in_visible_keys_with_shift_falls_back_to_normal_click()
    {
        var range = new RangeSelection<int>();
        var visible = new List<int> { 1, 2, 3, 4, 5 };
        var selected = new HashSet<int>();

        // Establish anchor at 2
        range.HandleClick(2, shiftKey: false, visible, selected);
        Assert.Equal(2, range.AnchorKey);

        // Shift+Click item 999 which is NOT in visibleKeys
        range.HandleClick(999, shiftKey: true, visible, selected);

        // Should fall back to normal click on 999: added to selected, anchor updated to 999
        Assert.Contains(999, selected);
        Assert.Equal(999, range.AnchorKey);
        Assert.True(range.AnchorState);
    }

    [Fact]
    public void Clicked_item_not_in_visible_keys_without_shift_acts_as_normal_click()
    {
        var range = new RangeSelection<int>();
        var visible = new List<int> { 1, 2, 3 };
        var selected = new HashSet<int>();

        range.HandleClick(999, shiftKey: false, visible, selected);

        Assert.Contains(999, selected);
        Assert.Equal(999, range.AnchorKey);
        Assert.True(range.AnchorState);
    }

    [Fact]
    public void Empty_visible_keys_list_does_not_throw_and_handles_gracefully()
    {
        var range = new RangeSelection<int>();
        var emptyVisible = Array.Empty<int>();
        var selected = new HashSet<int>();

        // 1. Normal click on empty list
        range.HandleClick(100, shiftKey: false, emptyVisible, selected);
        Assert.Contains(100, selected);
        Assert.Equal(100, range.AnchorKey);
        Assert.True(range.AnchorState);

        // 2. Shift-click on empty list (anchor 100 is not in visible keys)
        range.HandleClick(200, shiftKey: true, emptyVisible, selected);
        Assert.Contains(200, selected);
        Assert.Equal(200, range.AnchorKey);
        Assert.True(range.AnchorState);

        // 3. Normal click to toggle off
        range.HandleClick(200, shiftKey: false, emptyVisible, selected);
        Assert.DoesNotContain(200, selected);
        Assert.Equal(200, range.AnchorKey);
        Assert.False(range.AnchorState);

        // 4. Shift-click on empty list again with AnchorState false
        range.HandleClick(100, shiftKey: true, emptyVisible, selected);
        // Anchor 200 not in empty list -> fallback to normal click on 100 -> 100 is toggled off
        Assert.DoesNotContain(100, selected);
        Assert.Equal(100, range.AnchorKey);
        Assert.False(range.AnchorState);
    }

    [Fact]
    public void Single_item_list_behavior()
    {
        var range = new RangeSelection<int>();
        var singleItem = new List<int> { 42 };
        var selected = new HashSet<int>();

        // Click single item without shift
        range.HandleClick(42, shiftKey: false, singleItem, selected);
        Assert.Contains(42, selected);
        Assert.Equal(42, range.AnchorKey);
        Assert.True(range.AnchorState);

        // Shift-click same single item
        range.HandleClick(42, shiftKey: true, singleItem, selected);
        Assert.Contains(42, selected);
        Assert.Equal(42, range.AnchorKey);
        Assert.True(range.AnchorState);

        // Normal click deselects
        range.HandleClick(42, shiftKey: false, singleItem, selected);
        Assert.DoesNotContain(42, selected);
        Assert.Equal(42, range.AnchorKey);
        Assert.False(range.AnchorState);

        // Shift-click while unselected leaves unselected
        range.HandleClick(42, shiftKey: true, singleItem, selected);
        Assert.DoesNotContain(42, selected);
        Assert.Equal(42, range.AnchorKey);
        Assert.False(range.AnchorState);
    }

    [Fact]
    public void Rapid_chaotic_shift_key_toggling_preserves_state_integrity()
    {
        var range = new RangeSelection<int>();
        var visible = Enumerable.Range(1, 20).ToList();
        var selected = new HashSet<int>();

        // Click 5 (normal, select)
        range.HandleClick(5, shiftKey: false, visible, selected);
        Assert.Contains(5, selected);
        Assert.Equal(5, range.AnchorKey);

        // Shift-click 8 (range 5..8 selected)
        range.HandleClick(8, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 5, 6, 7, 8 }, selected.OrderBy(x => x));

        // Normal click 6 (unselects 6, anchor becomes 6, AnchorState = false)
        range.HandleClick(6, shiftKey: false, visible, selected);
        Assert.DoesNotContain(6, selected);
        Assert.Equal(6, range.AnchorKey);
        Assert.False(range.AnchorState);

        // Shift-click 4 (unselects 4..6, anchor remains 6)
        range.HandleClick(4, shiftKey: true, visible, selected);
        Assert.DoesNotContain(4, selected);
        Assert.DoesNotContain(5, selected);
        Assert.DoesNotContain(6, selected);
        Assert.Contains(7, selected);
        Assert.Contains(8, selected);

        // Reset
        range.Reset();
        Assert.False(range.HasAnchor);

        // Shift-click 15 after reset (no anchor -> falls back to normal click)
        range.HandleClick(15, shiftKey: true, visible, selected);
        Assert.Contains(15, selected);
        Assert.Equal(15, range.AnchorKey);
        Assert.True(range.AnchorState);
    }

    // =========================================================================
    // 4. Custom Key Types, Comparers & Records
    // =========================================================================

    public record CompositeListingKey(int ShopId, string Section, long ListingId);

    [Fact]
    public void Custom_composite_record_keys()
    {
        var range = new RangeSelection<CompositeListingKey>();
        var visible = new List<CompositeListingKey>
        {
            new(1, "A", 101L),
            new(1, "A", 102L),
            new(1, "B", 103L),
            new(2, "C", 201L),
            new(2, "C", 202L)
        };
        var selected = new HashSet<CompositeListingKey>();

        // Click index 1
        var key1 = visible[1];
        range.HandleClick(key1, shiftKey: false, visible, selected);
        Assert.Equal(key1, range.AnchorKey);

        // Shift-click index 3
        var key3 = visible[3];
        range.HandleClick(key3, shiftKey: true, visible, selected);

        Assert.Equal(3, selected.Count);
        Assert.Contains(visible[1], selected);
        Assert.Contains(visible[2], selected);
        Assert.Contains(visible[3], selected);
        Assert.DoesNotContain(visible[0], selected);
        Assert.DoesNotContain(visible[4], selected);
    }

    public readonly record struct TableCellKey(int Row, int Col);

    [Fact]
    public void Custom_record_struct_keys()
    {
        var range = new RangeSelection<TableCellKey>();
        var visible = new List<TableCellKey>
        {
            new(0, 0),
            new(0, 1),
            new(1, 0),
            new(1, 1),
            new(2, 0)
        };
        var selected = new HashSet<TableCellKey>();

        range.HandleClick(new(0, 1), shiftKey: false, visible, selected);
        range.HandleClick(new(2, 0), shiftKey: true, visible, selected);

        Assert.Equal(4, selected.Count);
        Assert.DoesNotContain(new(0, 0), selected);
        Assert.Contains(new(0, 1), selected);
        Assert.Contains(new(1, 0), selected);
        Assert.Contains(new(1, 1), selected);
        Assert.Contains(new(2, 0), selected);
    }

    [Fact]
    public void String_keys_with_special_characters_and_whitespace()
    {
        var range = new RangeSelection<string>();
        var visible = new List<string>
        {
            "",
            "   ",
            "SKU-100/v1",
            "Listing #42 & More",
            "Emoji-🎉-Test",
            "Newline\nItem"
        };
        var selected = new HashSet<string>();

        // Click empty string key
        range.HandleClick("", shiftKey: false, visible, selected);
        Assert.Contains("", selected);
        Assert.Equal("", range.AnchorKey);

        // Shift-click emoji key
        range.HandleClick("Emoji-🎉-Test", shiftKey: true, visible, selected);
        Assert.Equal(5, selected.Count);
        Assert.Contains("", selected);
        Assert.Contains("   ", selected);
        Assert.Contains("SKU-100/v1", selected);
        Assert.Contains("Listing #42 & More", selected);
        Assert.Contains("Emoji-🎉-Test", selected);
        Assert.DoesNotContain("Newline\nItem", selected);
    }

    [Fact]
    public void Nullable_string_keys_do_not_throw_null_reference_exception()
    {
        var range = new RangeSelection<string?>();
        var visible = new List<string?> { "A", null, "B", "C" };
        var selected = new HashSet<string?>();

        // Click "A"
        range.HandleClick("A", shiftKey: false, visible, selected);
        Assert.Equal("A", range.AnchorKey);

        // Shift-click "C" over the null key
        range.HandleClick("C", shiftKey: true, visible, selected);
        Assert.Contains("A", selected);
        Assert.Contains(null, selected);
        Assert.Contains("B", selected);
        Assert.Contains("C", selected);

        // Click null key directly without shift
        range.HandleClick(null, shiftKey: false, visible, selected);
        // Toggles null off
        Assert.DoesNotContain(null, selected);
        Assert.Null(range.AnchorKey);
        Assert.False(range.AnchorState);
    }

    // =========================================================================
    // 5. Dynamic Sorting, Filtering & CanSelect Complex Predicates
    // =========================================================================

    [Fact]
    public void Dynamic_resorting_between_clicks_tracks_items_in_new_sort_order()
    {
        var range = new RangeSelection<string>();
        // Initial order ascending
        var ascending = new List<string> { "Apple", "Banana", "Cherry", "Date", "Elderberry" };
        var selected = new HashSet<string>();

        // Click "Banana"
        range.HandleClick("Banana", shiftKey: false, ascending, selected);
        Assert.Equal("Banana", range.AnchorKey);

        // User sorts descending: ["Elderberry", "Date", "Cherry", "Banana", "Apple"]
        var descending = new List<string> { "Elderberry", "Date", "Cherry", "Banana", "Apple" };

        // User Shift+Clicks "Date"
        // In descending order: "Date" is index 1, "Banana" is index 3.
        // Range must be indices 1..3: "Date", "Cherry", "Banana"
        range.HandleClick("Date", shiftKey: true, descending, selected);

        Assert.Contains("Date", selected);
        Assert.Contains("Cherry", selected);
        Assert.Contains("Banana", selected);
        Assert.DoesNotContain("Elderberry", selected);
        Assert.DoesNotContain("Apple", selected);
    }

    [Fact]
    public void CanSelect_predicate_blocks_unselectable_target_and_skips_intermediate()
    {
        var range = new RangeSelection<int>();
        var visible = new List<int> { 0, 1, 2, 3, 4, 5, 6, 7, 8 };
        var selected = new HashSet<int>();

        // Only even numbers are selectable
        bool CanSelectEven(int k) => k % 2 == 0;

        // Click 2 (selectable) -> anchor = 2
        range.HandleClick(2, shiftKey: false, visible, selected, CanSelectEven);
        Assert.Equal(new[] { 2 }, selected);

        // Shift-click 7 (unselectable target!)
        // In the range 2..7, only 2, 4, 6 should be selected, 3, 5, 7 skipped
        range.HandleClick(7, shiftKey: true, visible, selected, CanSelectEven);

        Assert.Equal(new[] { 2, 4, 6 }, selected.OrderBy(x => x));
        Assert.DoesNotContain(3, selected);
        Assert.DoesNotContain(5, selected);
        Assert.DoesNotContain(7, selected);
    }

    [Fact]
    public void CanSelect_all_false_leaves_state_completely_unmodified()
    {
        var range = new RangeSelection<int>();
        var visible = new List<int> { 1, 2, 3 };
        var selected = new HashSet<int>();

        bool NeverSelect(int _) => false;

        // Normal click with all-false predicate
        range.HandleClick(1, shiftKey: false, visible, selected, NeverSelect);
        Assert.Empty(selected);
        Assert.False(range.HasAnchor);
        Assert.Equal(0, range.AnchorKey);

        // Shift-click with all-false predicate
        range.HandleClick(2, shiftKey: true, visible, selected, NeverSelect);
        Assert.Empty(selected);
        Assert.False(range.HasAnchor);
    }

    [Fact]
    public void Repeated_shift_clicks_on_anchor_are_idempotent()
    {
        var range = new RangeSelection<int>();
        var visible = new List<int> { 10, 20, 30 };
        var selected = new HashSet<int>();

        range.HandleClick(20, shiftKey: false, visible, selected);
        Assert.Equal(new[] { 20 }, selected);

        // Repeated Shift+Clicks on 20
        range.HandleClick(20, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 20 }, selected);

        range.HandleClick(20, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 20 }, selected);
        Assert.Equal(20, range.AnchorKey);
        Assert.True(range.AnchorState);
    }

    [Fact]
    public void ClearAnchor_allows_subsequent_shift_click_to_reanchor_gracefully()
    {
        var range = new RangeSelection<int>();
        var visible = new List<int> { 1, 2, 3, 4, 5 };
        var selected = new HashSet<int>();

        range.HandleClick(2, shiftKey: false, visible, selected);
        Assert.True(range.HasAnchor);

        range.ClearAnchor();
        Assert.False(range.HasAnchor);

        // Shift-click after clear anchor acts as a normal click establishing a new anchor
        range.HandleClick(4, shiftKey: true, visible, selected);
        Assert.True(range.HasAnchor);
        Assert.Equal(4, range.AnchorKey);
        Assert.Equal(new[] { 2, 4 }, selected.OrderBy(x => x));
    }
}
