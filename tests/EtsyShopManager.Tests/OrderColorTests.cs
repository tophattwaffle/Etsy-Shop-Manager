using System.Text;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Modules.Orders;

namespace EtsyShopManager.Tests;

public class OrderColorTests
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
    public async Task Default_list_is_the_one_the_dashboard_card_had()
    {
        var config = await new OrderColorService(new MemorySettings()).GetAsync();

        Assert.Equal(99, config.Colors.Count);
        Assert.Equal(["#B87333"], config.Colors.Single(c => c.Name == "Copper").Hex);
        Assert.True(config.Colors.Single(c => c.Name == "Copper").Metallic);
        Assert.Equal(["#4FC3F7", "#FF1FD1"], config.Colors.Single(c => c.Name == "Neon Rider").Hex);
        Assert.Equal(["random"], config.RandomWords);
    }

    [Fact]
    public async Task Saved_list_is_what_the_card_receives()
    {
        var settings = new MemorySettings();
        var service = new OrderColorService(settings);
        var before = (await service.GetAsync()).ToCardJson();

        await service.SaveAsync(new OrderColorConfig(
            [new OrderColor(" Teal ", ["#008080"], false), new OrderColor("Copper", ["#b87333"], true)], ["random", "surprise me"]), "tophattwaffle");
        var json = (await service.GetAsync()).ToCardJson();

        Assert.Equal("#008080", (string)json["colors"]!["Teal"]![0]!);
        Assert.Equal("#B87333", (string)json["colors"]!["Copper"]![0]!);
        Assert.Equal(["Copper"], json["metallic"]!.AsArray().Select(n => (string)n!));
        Assert.Equal(["random", "surprise me"], json["random_words"]!.AsArray().Select(n => (string)n!));
        Assert.NotEqual((string)before["version"]!, (string)json["version"]!);
    }

    [Fact]
    public async Task Invalid_lists_are_refused_with_reasons()
    {
        var problems = OrderColorService.Validate(new OrderColorConfig(
            [new OrderColor("Red", ["red"], false), new OrderColor("red", ["#FF0000"], false), new OrderColor("", ["#000000"], false),
             new OrderColor("Empty", [], false)], []));

        Assert.Contains(problems, p => p.Contains("\"red\" isn't a color"));
        Assert.Contains(problems, p => p.Contains("listed twice"));
        Assert.Contains(problems, p => p.Contains("needs a name"));
        Assert.Contains(problems, p => p.Contains("needs at least one color"));
        await Assert.ThrowsAsync<ArgumentException>(() => new OrderColorService(new MemorySettings())
            .SaveAsync(new OrderColorConfig([new OrderColor("Red", ["red"], false)], []), null));
    }

    [Fact]
    public void Card_json_round_trips_through_the_parser()
    {
        var json = OrderColorService.Default.ToCardJson();
        var back = OrderColorService.Parse(json.ToJsonString()).ToCardJson();
        Assert.Equal(json.ToJsonString(), back.ToJsonString());
    }

    /// <summary>
    /// The app serves the HA card file itself (embedded at build) and colors orders with its engine, so the two can't
    /// drift. This fails if the embedded copy isn't the repo's card, or the card stops exposing what the app uses.
    /// </summary>
    [Fact]
    public void App_runs_the_same_card_file_as_Home_Assistant()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "EtsyShopManager.slnx"))) root = root.Parent!;
        var card = File.ReadAllText(Path.Combine(root.FullName, "homeassistant", "www", "etsy-orders-card.js"));

        Assert.Equal(card, Encoding.UTF8.GetString(OrderCardScript.Content));
        Assert.Contains("window.EtsyOrderColors = EtsyOrderColors;", card);
        Assert.Contains("static CSS = ", card);
        Assert.Contains("box(text, seed, small)", card);
        Assert.Contains("attributes?.color_config", card); // the card reads the list the integration passes on
        Assert.StartsWith("js/etsy-orders-card.js?v=", OrderCardScript.VersionedUrl);
    }

    [Fact]
    public void Shop_accents_ride_along_by_Etsy_shop_id_and_change_the_version()
    {
        var plain = OrderColorService.Default.ToCardJson();
        var json = OrderColorService.Default.ToCardJson(new Dictionary<string, string> { ["43"] = "#FF8800", ["42"] = "#6BC7BE" });

        Assert.Null(plain["shop_accents"]);
        Assert.Equal("#FF8800", (string)json["shop_accents"]!["43"]!);
        Assert.Equal(["42", "43"], json["shop_accents"]!.AsObject().Select(kv => kv.Key));
        Assert.NotEqual((string)plain["version"]!, (string)json["version"]!);
    }

    [Fact]
    public void Card_json_keys_are_the_card_config_keys()
    {
        var json = OrderColorService.Default.ToCardJson();
        Assert.Equal(["colors", "metallic", "random_words", "version"], json.Select(kv => kv.Key));
        Assert.IsType<JsonArray>(json["colors"]!["Pastel Rainbow"]);
    }
}
