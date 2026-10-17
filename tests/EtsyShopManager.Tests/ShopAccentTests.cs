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
}
