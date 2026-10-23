using EtsyShopManager.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MudBlazor.Services;
using Xunit;

namespace EtsyShopManager.Tests;

#pragma warning disable BL0005 // Component parameter should not be set outside of its component in unit test
public class RangeCheckBoxTests
{
    private sealed class TestJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => default;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => default;
    }

    private static async Task<string> RenderRangeCheckBoxHtmlAsync(bool value, bool disabled = false, string? ariaLabel = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<Microsoft.JSInterop.IJSRuntime, TestJsRuntime>();
        services.AddMudServices();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                ["Value"] = value,
                ["Disabled"] = disabled,
                ["AriaLabel"] = ariaLabel,
            });
            var output = await renderer.RenderComponentAsync<RangeCheckBox>(parameters);
            return output.ToHtmlString();
        });
    }

    [Fact]
    public async Task Render_WhenUnchecked_RendersSpanWithoutLabel_AndUncheckedInputAndSvg()
    {
        var html = await RenderRangeCheckBoxHtmlAsync(value: false, ariaLabel: "Select item 1");

        // Critical requirement: MUST NOT render a <label> (which causes synthetic click double-firing)
        Assert.DoesNotContain("<label", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<span class=\"mud-checkbox", html);
        Assert.Contains("<input type=\"checkbox\"", html);
        Assert.Contains("class=\"mud-checkbox-input\"", html);
        Assert.Contains("aria-label=\"Select item 1\"", html);
        Assert.DoesNotContain("checked", html);
        Assert.Contains("mud-checkbox-false", html);
        // Unchecked box SVG path
        Assert.Contains("M19 5v14H5V5h14", html);
    }

    [Fact]
    public async Task Render_WhenChecked_RendersCheckedInputAndCheckmarkSvg()
    {
        var html = await RenderRangeCheckBoxHtmlAsync(value: true, ariaLabel: "Select item 2");

        Assert.DoesNotContain("<label", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<input type=\"checkbox\"", html);
        Assert.Contains("checked", html);
        Assert.Contains("mud-checkbox-true", html);
        Assert.Contains("mud-primary-text", html);
        // Checked checkmark SVG path
        Assert.Contains("M19 3H5", html);
    }

    [Fact]
    public async Task Render_WhenDisabled_RendersDisabledInputAndDisabledClass()
    {
        var html = await RenderRangeCheckBoxHtmlAsync(value: false, disabled: true);

        Assert.Contains("disabled", html);
        Assert.Contains("mud-disabled", html);
    }

    [Fact]
    public async Task BrowserEventSimulation_SingleClick_TriggersClickedExactlyOnce()
    {
        int clickCount = 0;
        MouseEventArgs? receivedArgs = null;

        var component = new RangeCheckBox
        {
            Clicked = EventCallback.Factory.Create<MouseEventArgs>(this, (MouseEventArgs e) =>
            {
                clickCount++;
                receivedArgs = e;
            }),
            Value = false,
        };

        var onClickMethod = typeof(RangeCheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var args = new MouseEventArgs { ShiftKey = false };
        await (Task)onClickMethod.Invoke(component, [args])!;

        Assert.Equal(1, clickCount);
        Assert.NotNull(receivedArgs);
        Assert.False(receivedArgs.ShiftKey);
    }

    [Fact]
    public async Task BrowserEventSimulation_ShiftClick_PreservesShiftKeyFlag()
    {
        MouseEventArgs? receivedArgs = null;

        var component = new RangeCheckBox
        {
            Clicked = EventCallback.Factory.Create<MouseEventArgs>(this, (MouseEventArgs e) =>
            {
                receivedArgs = e;
            }),
            Value = false,
        };

        var onClickMethod = typeof(RangeCheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var args = new MouseEventArgs { ShiftKey = true };
        await (Task)onClickMethod.Invoke(component, [args])!;

        Assert.NotNull(receivedArgs);
        Assert.True(receivedArgs.ShiftKey);
    }

    [Fact]
    public async Task BrowserEventSimulation_WhenDisabled_ClickIsIgnored()
    {
        int clickCount = 0;

        var component = new RangeCheckBox
        {
            Clicked = EventCallback.Factory.Create<MouseEventArgs>(this, (MouseEventArgs _) =>
            {
                clickCount++;
            }),
            Disabled = true,
            Value = false,
        };

        var onClickMethod = typeof(RangeCheckBox).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        await (Task)onClickMethod.Invoke(component, [new MouseEventArgs()])!;

        Assert.Equal(0, clickCount);
    }

    [Fact]
    public void Comparison_DefectiveLabelWrapping_Versus_RangeCheckBox_Demonstration()
    {
        // Demonstrates the exact bug:
        // In a real browser, wrapping <MudCheckBox> inside <td @onclick="..."> causes:
        // 1. Click on <label>/<svg> bubbles to <td> -> HandleClick #1 (item selected, anchor=true)
        // 2. Synthetic click on <input> bubbles to <td> -> HandleClick #2 (item DESELECTED, anchor=false!)
        // Whereas RangeCheckBox has NO <label>, firing only 1 click event.

        var range = new RangeSelection<int>();
        var selected = new HashSet<int>();
        var visible = new[] { 101, 102, 103 };

        // Defective double-firing sequence simulation:
        // Click 1: label click
        range.HandleClick(101, shiftKey: false, visible, selected);
        Assert.Contains(101, selected);
        Assert.True(range.AnchorState); // momentarily true

        // Click 2: synthetic input click bubbling to <td>
        range.HandleClick(101, shiftKey: false, visible, selected);
        Assert.DoesNotContain(101, selected); // DESELECTED!
        Assert.False(range.AnchorState); // RUINED ANCHOR!

        // Now with RangeCheckBox: Exactly ONE event fires
        range.Reset();
        selected.Clear();

        // Checkbox 101 clicked once:
        range.HandleClick(101, shiftKey: false, visible, selected);
        Assert.Contains(101, selected);
        Assert.True(range.AnchorState);
        Assert.Equal(101, range.AnchorKey);

        // Shift+Click checkbox 103:
        range.HandleClick(103, shiftKey: true, visible, selected);
        Assert.Equal(new[] { 101, 102, 103 }, selected.OrderBy(x => x));
        Assert.True(range.AnchorState);
        Assert.Equal(101, range.AnchorKey);
    }
}
