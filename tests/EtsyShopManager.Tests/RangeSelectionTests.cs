using EtsyShopManager.Web.Components.Shared;
using Xunit;

namespace EtsyShopManager.Tests;

public class RangeSelectionTests
{
    [Fact]
    public void Forward_range_selection_selects_all_items_between_anchor_and_target()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 1, 2, 3, 4, 5, 6 };
        var selected = new HashSet<int>();

        // Click item 2 without Shift
        range.HandleClick(2, shiftKey: false, visible, selected);
        Assert.Equal(new[] { 2 }, selected);
        Assert.Equal(2, range.AnchorKey);
        Assert.True(range.AnchorState);

        // Shift+Click item 5
        range.HandleClick(5, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 2, 3, 4, 5 }, selected.OrderBy(x => x));
        Assert.Equal(2, range.AnchorKey); // Anchor remains unchanged
        Assert.True(range.AnchorState);
    }

    [Fact]
    public void Backward_range_selection_selects_all_items_between_target_and_anchor()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 1, 2, 3, 4, 5, 6 };
        var selected = new HashSet<int>();

        // Click item 5 without Shift
        range.HandleClick(5, shiftKey: false, visible, selected);
        Assert.Equal(new[] { 5 }, selected);
        Assert.Equal(5, range.AnchorKey);
        Assert.True(range.AnchorState);

        // Shift+Click item 2 (moving backward)
        range.HandleClick(2, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 2, 3, 4, 5 }, selected.OrderBy(x => x));
        Assert.Equal(5, range.AnchorKey); // Anchor stays 5
        Assert.True(range.AnchorState);
    }

    [Fact]
    public void Deselection_range_unchecks_items_in_range()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 1, 2, 3, 4, 5, 6 };
        var selected = new HashSet<int> { 1, 2, 3, 4, 5, 6 };

        // Normal click on item 2 unchecks it (AnchorState becomes false)
        range.HandleClick(2, shiftKey: false, visible, selected);
        Assert.DoesNotContain(2, selected);
        Assert.Equal(2, range.AnchorKey);
        Assert.False(range.AnchorState);

        // Shift+Click on item 5 deselects the range [2..5]
        range.HandleClick(5, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 1, 6 }, selected.OrderBy(x => x));
        Assert.Equal(2, range.AnchorKey);
        Assert.False(range.AnchorState);
    }

    [Fact]
    public void Filtered_list_order_follows_visible_keys_skipping_hidden_items()
    {
        var range = new RangeSelection<int>();
        // Only items 1, 3, 5, 7 are visible in current filter
        var visible = new[] { 1, 3, 5, 7 };
        var selected = new HashSet<int>();

        range.HandleClick(3, shiftKey: false, visible, selected);
        range.HandleClick(7, shiftKey: true, visible, selected);

        // Selected should only contain the visible keys in that range
        Assert.Equal(new[] { 3, 5, 7 }, selected.OrderBy(x => x));
        Assert.DoesNotContain(2, selected);
        Assert.DoesNotContain(4, selected);
        Assert.DoesNotContain(6, selected);
    }

    [Fact]
    public void Sorted_list_order_follows_visible_sort_order()
    {
        var range = new RangeSelection<int>();
        // Table is sorted descending
        var visible = new[] { 50, 40, 30, 20, 10 };
        var selected = new HashSet<int>();

        // Click index 1 (item 40)
        range.HandleClick(40, shiftKey: false, visible, selected);
        // Shift+Click index 3 (item 20)
        range.HandleClick(20, shiftKey: true, visible, selected);

        // Should select indices 1, 2, 3 -> items 40, 30, 20
        Assert.Equal(new[] { 20, 30, 40 }, selected.OrderBy(x => x));
        Assert.DoesNotContain(50, selected);
        Assert.DoesNotContain(10, selected);
    }

    [Fact]
    public void CanSelect_predicate_skips_unselectable_rows_during_range_selection()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 10, 20, 30, 40, 50 };
        var selected = new HashSet<int>();

        // Item 30 is blocked / unselectable
        bool CanSelect(int k) => k != 30;

        range.HandleClick(20, shiftKey: false, visible, selected, CanSelect);
        Assert.Equal(new[] { 20 }, selected);

        range.HandleClick(40, shiftKey: true, visible, selected, CanSelect);
        // 20 and 40 selected; 30 skipped because CanSelect returned false
        Assert.Equal(new[] { 20, 40 }, selected.OrderBy(x => x));
        Assert.DoesNotContain(30, selected);
    }

    [Fact]
    public void CanSelect_predicate_blocks_normal_click_on_unselectable_row()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 10, 20, 30 };
        var selected = new HashSet<int>();

        bool CanSelect(int k) => k != 30;

        range.HandleClick(30, shiftKey: false, visible, selected, CanSelect);
        Assert.Empty(selected);
        Assert.False(range.HasAnchor);
        Assert.Equal(0, range.AnchorKey);
    }

    [Fact]
    public void Consecutive_shift_clicks_extend_and_contract_from_same_anchor()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 1, 2, 3, 4, 5, 6, 7 };
        var selected = new HashSet<int>();

        // Anchor at 3
        range.HandleClick(3, shiftKey: false, visible, selected);
        Assert.Equal(new[] { 3 }, selected);

        // First shift+click to 5 -> selects 3, 4, 5
        range.HandleClick(5, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 3, 4, 5 }, selected.OrderBy(x => x));

        // Second shift+click to 7 -> selects 6, 7 (anchor is still 3, range 3..7)
        range.HandleClick(7, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 3, 4, 5, 6, 7 }, selected.OrderBy(x => x));

        // Shift+click backward to 1 -> selects 1, 2 (anchor is still 3, range 1..3)
        range.HandleClick(1, shiftKey: true, visible, selected);
        Assert.Contains(1, selected);
        Assert.Contains(2, selected);
        Assert.Contains(3, selected);
    }

    [Fact]
    public void Composite_keys_work_seamlessly()
    {
        var range = new RangeSelection<(int ShopId, long PropertyId)>();
        var visible = new (int, long)[]
        {
            (1, 100L),
            (1, 200L),
            (2, 300L),
            (2, 400L)
        };
        var selected = new HashSet<(int, long)>();

        // Click first
        range.HandleClick((1, 100L), shiftKey: false, visible, selected);
        Assert.Contains((1, 100L), selected);
        Assert.Equal((1, 100L), range.AnchorKey);

        // Shift+Click third
        range.HandleClick((2, 300L), shiftKey: true, visible, selected);
        Assert.Contains((1, 100L), selected);
        Assert.Contains((1, 200L), selected);
        Assert.Contains((2, 300L), selected);
        Assert.DoesNotContain((2, 400L), selected);
    }

    [Fact]
    public void Fallback_when_no_anchor_is_set_or_anchor_not_in_visible_keys()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 10, 20, 30 };
        var selected = new HashSet<int>();

        // Shift+Click with NO prior anchor behaves as normal click
        range.HandleClick(20, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 20 }, selected);
        Assert.Equal(20, range.AnchorKey);
        Assert.True(range.AnchorState);

        // Now change visible list so anchor (20) is no longer visible
        var newVisible = new[] { 40, 50, 60 };
        range.HandleClick(50, shiftKey: true, newVisible, selected);

        // Since anchor 20 is not in newVisible, it falls back to normal click on 50
        Assert.Contains(50, selected);
        Assert.Equal(50, range.AnchorKey);
        Assert.True(range.AnchorState);
    }

    [Fact]
    public void Reset_clears_anchor_and_state()
    {
        var range = new RangeSelection<int>();
        var visible = new[] { 1, 2, 3 };
        var selected = new HashSet<int>();

        range.HandleClick(2, shiftKey: false, visible, selected);
        Assert.True(range.HasAnchor);
        Assert.Equal(2, range.AnchorKey);
        Assert.True(range.AnchorState);

        range.Reset();
        Assert.False(range.HasAnchor);
        Assert.Equal(0, range.AnchorKey);
        Assert.False(range.AnchorState);
    }
}
