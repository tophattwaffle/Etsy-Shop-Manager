using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Modules.Shops;

namespace EtsyShopManager.Tests;

public class ShopAccentTests
{
    private sealed class MemorySettings : ISettingsStore
    {
        public Dictionary<string, string?> Values { get; } = [];
        public Task<string?> GetAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.GetValueOrDefault(key));

        public Task SetAsync(string key, string? value, bool encrypt = false, string? updatedBy = null, CancellationToken ct = default)
        {
            Values[key] = value;
            return Task.CompletedTask;
        }

        public Task<bool> HasValueAsync(string key, CancellationToken ct = default) => Task.FromResult(Values.ContainsKey(key));
    }

    [Fact]
    public async Task Override_is_saved_per_shop_and_can_go_back_to_automatic()
    {
        var accents = new ShopAccentService(new MemorySettings(), new NullAuditLog());

        Assert.Empty(await accents.GetOverridesAsync([1, 2]));

        await accents.SetAsync(1, " #b87333 ", "tophattwaffle");
        Assert.Equal(new Dictionary<int, string> { [1] = "#B87333" }, await accents.GetOverridesAsync([1, 2]));

        await accents.SetAsync(1, null, "tophattwaffle");
        Assert.Empty(await accents.GetOverridesAsync([1, 2]));
    }

    [Fact]
    public async Task Effective_color_is_the_override_else_the_last_automatic_one()
    {
        var accents = new ShopAccentService(new MemorySettings(), new NullAuditLog());
        await accents.SaveAutomaticAsync(1, "#6bc7be");
        await accents.SaveAutomaticAsync(2, "#123456");
        await accents.SaveAutomaticAsync(3, "not a color");
        await accents.SetAsync(2, "#FF8800", "tophattwaffle");

        Assert.Equal(new Dictionary<int, string> { [1] = "#6BC7BE", [2] = "#FF8800" }, await accents.GetEffectiveAsync([1, 2, 3]));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#12345")]
    [InlineData("B87333")]
    public async Task Only_hex_colors_are_accepted(string value)
    {
        var accents = new ShopAccentService(new MemorySettings(), new NullAuditLog());
        await Assert.ThrowsAsync<ArgumentException>(() => accents.SetAsync(1, value, null));
    }

    [Fact]
    public async Task Order_link_override_is_saved_per_shop_and_can_revert_to_default()
    {
        var links = new ShopOrderLinkService(new MemorySettings(), new NullAuditLog());

        Assert.Empty(await links.GetOverridesAsync([1, 2]));
        Assert.Equal("https://www.etsy.com/your/orders/sold?order_id=987654", ShopOrderLinkService.FormatUrl(null, 987654));

        await links.SetAsync(1, ShopOrderLinkService.EdgeTemplate, "tophattwaffle");
        var overrides = await links.GetOverridesAsync([1, 2]);
        Assert.Equal(ShopOrderLinkService.EdgeTemplate, overrides[1]);
        Assert.Equal("microsoft-edge:https://www.etsy.com/your/orders/sold?order_id=987654",
            ShopOrderLinkService.FormatUrl(overrides[1], 987654));

        await links.SetAsync(1, ShopOrderLinkService.DefaultTemplate, "tophattwaffle");
        Assert.Empty(await links.GetOverridesAsync([1, 2]));
    }

    [Fact]
    public void Chrome_profile_templates_round_trip_and_only_http_links_open_in_a_new_tab()
    {
        var chrome = ShopOrderLinkService.ChromeTemplate("Profile 1");
        Assert.Equal("esm-chrome://Profile%201/https://www.etsy.com/your/orders/sold?order_id={receipt_id}", chrome);
        Assert.True(ShopOrderLinkService.TryGetChromeProfile(chrome, out var profile));
        Assert.Equal("Profile 1", profile);

        var formatted = ShopOrderLinkService.FormatUrl(chrome, 42);
        Assert.Equal("esm-chrome://Profile%201/https://www.etsy.com/your/orders/sold?order_id=42", formatted);
        Assert.False(ShopOrderLinkService.OpensInNewTab(formatted));
        Assert.False(ShopOrderLinkService.OpensInNewTab(ShopOrderLinkService.FormatUrl(ShopOrderLinkService.EdgeTemplate, 42)));
        Assert.True(ShopOrderLinkService.OpensInNewTab(ShopOrderLinkService.FormatUrl(null, 42)));
        Assert.Equal("https://example.com/42", ShopOrderLinkService.FormatUrl("https://example.com/%7Breceipt_id%7D", 42));
    }

    [Theory]
    [InlineData("https://www.etsy.com/your/orders/sold")]
    [InlineData("javascript:alert({receipt_id})")]
    [InlineData("not-a-scheme/{receipt_id}")]
    public async Task Invalid_order_link_templates_are_rejected(string template)
    {
        var links = new ShopOrderLinkService(new MemorySettings(), new NullAuditLog());
        await Assert.ThrowsAsync<ArgumentException>(() => links.SetAsync(1, template, null));
    }
}
