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

    private static OrderColorEngine Engine(OrderColorLook? look = null) =>
        new(OrderColorService.Default with { Look = look });

    [Fact]
    public void Listed_colors_tint_the_box_and_metallic_ones_shine()
    {
        var box = Engine().Box("Black & Copper", "1|0.0", small: true);

        Assert.Equal(" colored metal", box.Cls);
        Assert.Contains("color-mix(in srgb, #B87333 35%, var(--esm-color-base))", box.Style);
        Assert.Contains("--shine: 0.6;", box.Style);
        // Only the copper half shines: the metal mask is opaque over the second share alone.
        Assert.Contains("--metal-mask: linear-gradient(90deg, transparent 50.0%, #000 62.5%, #000 100%);", box.Style);
        Assert.Equal("", box.Inner);
    }

    [Fact]
    public void Css_color_names_count_and_words_join_but_other_text_stays_plain()
    {
        var engine = new OrderColorEngine(new OrderColorConfig([], ["random"]));

        Assert.Contains("darkgreen 0.0%", engine.Box("Dark  Green!", "s", small: true).Style);
        Assert.Same(OrderColorBox.None, engine.Box("Large, matte", "s", small: true));
        Assert.Same(OrderColorBox.None, engine.Box("Mark the field", "s", small: true)); // browser system colors don't count
    }

    [Fact]
    public void Multi_color_filaments_stack_their_colors()
    {
        var box = Engine().Box("Neon Rider", "s", small: false);

        Assert.Equal(" colored multi", box.Cls);
        Assert.Contains("--multi: linear-gradient(180deg, color-mix(in srgb, #4FC3F7 35%, var(--esm-color-base))", box.Style);
        Assert.Contains("--multi-mask: none;", box.Style);
    }

    [Fact]
    public void Two_filaments_side_by_side_fade_into_each_other()
    {
        var box = Engine().Box("one in nebula. and one in sunrise", "s", small: false);

        Assert.Equal(" colored multi", box.Cls);
        // Each filament's stack fades out where they meet, so the left-to-right blend joins them without a seam.
        Assert.Contains("--multi-mask: linear-gradient(90deg, #000 0%, #000 37.5%, transparent 50.0%, transparent 50.0%, #000 62.5%, #000 100%);", box.Style);
    }

    [Fact]
    public void Random_boxes_are_seeded_and_follow_the_look()
    {
        var engine = Engine();
        var pill = engine.Box("Random", "42|0.0", small: true);

        Assert.Equal(" colored random lava", pill.Cls);
        Assert.Equal(3, pill.Inner.Split("class=\"blob\"").Length - 1);
        Assert.Equal(pill, engine.Box("Random", "42|0.0", small: true));
        Assert.NotEqual(pill.Style, engine.Box("Random", "43|0.0", small: true).Style);
        Assert.Equal(4, engine.Box("Random please", "42|0.note", small: false).Inner.Split("class=\"blob\"").Length - 1);

        var confetti = Engine(new OrderColorLook(RandomStyle: OrderColorLook.Confetti, AnimateRandom: false)).Box("Random", "42|0.0", small: true);
        Assert.Equal(" colored random confetti still", confetti.Cls);
        Assert.StartsWith("--confetti: radial-gradient(", confetti.Style);
        Assert.Equal("", confetti.Inner);
    }

    [Fact]
    public void No_tint_means_no_coloring()
    {
        var engine = Engine(new OrderColorLook(Tint: 0));
        Assert.Same(OrderColorBox.None, engine.Box("Copper", "s", small: true));
        Assert.Same(OrderColorBox.None, engine.Box("Random", "s", small: true));
        Assert.DoesNotContain("--shine", Engine(new OrderColorLook(MetallicShine: 0)).Box("Copper", "s", small: true).Style);
    }

    /// <summary>
    /// The HA card no longer colors anything itself: every colored variation and personalization in the payload comes
    /// drawn, seeded by receipt and position as the card did, and the To make rows are left as they were.
    /// </summary>
    [Fact]
    public void Payload_boxes_come_painted()
    {
        var payload = JsonNode.Parse("""
            {"shops":[{"items_needed":[{"variations":[{"property":"Color","value":"Copper"}]}],
              "open_orders":[{"receipt_id":"77","items":[
                {"variations":[{"property":"Size","value":"Large"},{"property":"Color","value":"Copper"}],"personalization":"Random please"},
                {"variations":[]}]}]}]}
            """)!.AsObject();
        var engine = Engine();

        engine.Paint(payload);

        var items = payload["shops"]![0]!["open_orders"]![0]!["items"]!;
        Assert.Null(items[0]!["variations"]![0]!["paint"]);
        var paint = items[0]!["variations"]![1]!["paint"]!;
        Assert.Equal(engine.Box("Copper", "77|0.1", small: true).ToJson().ToJsonString(), paint.ToJsonString());
        Assert.Equal(engine.Box("Random please", "77|0.note", small: false).Style, (string?)items[0]!["personalization_paint"]!["style"]);
        Assert.Null(items[1]!["personalization_paint"]);
        Assert.Null(payload["shops"]![0]!["items_needed"]![0]!["variations"]![0]!["paint"]);
    }

    [Fact]
    public async Task Look_is_saved_with_the_list_and_checked()
    {
        var settings = new MemorySettings();
        var service = new OrderColorService(settings);
        var look = new OrderColorLook(Tint: 50, MetallicShine: 0, AnimateMetallic: false, RandomStyle: OrderColorLook.Confetti,
            RandomMinBrightness: 40, AnimateRandom: false);

        await service.SaveAsync(OrderColorService.Default with { Look = look }, "tophattwaffle");

        Assert.Equal(look, (await service.GetAsync()).Style);
        Assert.Equal(OrderColorLook.Default, OrderColorService.Default.Style);
        var problems = OrderColorService.Validate(OrderColorService.Default with { Look = new OrderColorLook(Tint: 101, RandomStyle: "sparkle") });
        Assert.Contains(problems, p => p.Contains("Tint strength"));
        Assert.Contains(problems, p => p.Contains("lava or confetti"));
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
        Assert.Equal(["colors", "metallic", "random_words", "color_tint", "metallic_shine", "animate_metallic", "random_style",
            "random_min_brightness", "animate_random", "version"], json.Select(kv => kv.Key));
        Assert.IsType<JsonArray>(json["colors"]!["Pastel Rainbow"]);
        Assert.Equal(OrderColorEngine.Css, (string?)OrderColorService.Default.ToCardJson(css: OrderColorEngine.Css)["css"]);
    }

    [Fact]
    public async Task Metallic_and_random_tint_strengths_are_independent()
    {
        var defaultEngine = Engine();
        var defaultRandom = defaultEngine.Box("Random", "42|0.0", small: true);
        var defaultBlack = defaultEngine.Box("Black", "42|0.0", small: true);

        // Changing metallic tint changes metallic colors (Copper) without touching Random or non-metallic colors (Black).
        var metalChanged = Engine(new OrderColorLook(Tint: 35, MetallicTint: 75, RandomTint: 35));
        Assert.Contains("color-mix(in srgb, #B87333 75%, var(--esm-color-base))", metalChanged.Box("Copper", "42|0.0", small: true).Style);
        Assert.Equal(defaultRandom, metalChanged.Box("Random", "42|0.0", small: true));
        Assert.Equal(defaultBlack, metalChanged.Box("Black", "42|0.0", small: true));

        // Changing random tint changes Random without touching Copper or Black.
        var randomChanged = Engine(new OrderColorLook(Tint: 35, MetallicTint: 75, RandomTint: 65));
        Assert.NotEqual(defaultRandom.Style, randomChanged.Box("Random", "42|0.0", small: true).Style);
        Assert.Equal(metalChanged.Box("Copper", "42|0.0", small: true), randomChanged.Box("Copper", "42|0.0", small: true));

        var settings = new MemorySettings();
        var service = new OrderColorService(settings);
        var look = new OrderColorLook(Tint: 35, MetallicTint: 75, RandomTint: 65);
        await service.SaveAsync(OrderColorService.Default with { Look = look }, "tophattwaffle");
        Assert.Equal(look, (await service.GetAsync()).Style);
    }
}
