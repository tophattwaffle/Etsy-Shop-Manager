using System.Security.Cryptography;
using System.Text;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Services;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>One named color: a filament or finish, with one or more hex colors (several for multi-color filaments).</summary>
public sealed record OrderColor(string Name, IReadOnlyList<string> Hex, bool Metallic);

/// <summary>
/// How colored boxes look: tint strength (%, 0 turns coloring off), metal sheen strength (%, 0 turns it off), the
/// "random" effect (lava or confetti) and the brightness (%) a list color needs to be used in it, and whether the
/// metal glint and the random effect move.
/// </summary>
public sealed record OrderColorLook(int Tint = 35, int MetallicShine = 60, bool AnimateMetallic = true,
    string RandomStyle = OrderColorLook.Lava, int RandomMinBrightness = 60, bool AnimateRandom = true)
{
    public const string Lava = "lava";
    public const string Confetti = "confetti";
    public static readonly OrderColorLook Default = new();
}

/// <summary>
/// The order color list: which words in variations and personalization get tinted, and how. The app is the source of
/// truth: <see cref="OrderColorEngine"/> draws the boxes for the Orders page and for the Home Assistant payload, so the
/// card only paints what it is sent.
/// </summary>
public sealed record OrderColorConfig(IReadOnlyList<OrderColor> Colors, IReadOnlyList<string> RandomWords, OrderColorLook? Look = null)
{
    /// <summary>The look settings, the defaults when none were saved.</summary>
    public OrderColorLook Style => Look ?? OrderColorLook.Default;

    /// <summary>
    /// The card's config keys (<c>colors</c>, <c>metallic</c>, <c>random_words</c> and the look keys, which cards before
    /// 3.0 still read), the shops' accent colors by Etsy shop id (<c>shop_accents</c>, when given), the engine's
    /// <c>css</c> (when given) and a <c>version</c> hash of all of it.
    /// </summary>
    public JsonObject ToCardJson(IReadOnlyDictionary<string, string>? shopAccents = null, string? css = null)
    {
        var colors = new JsonObject();
        foreach (var c in Colors) colors[c.Name] = new JsonArray(c.Hex.Select(h => (JsonNode)h).ToArray());
        var json = new JsonObject
        {
            ["colors"] = colors,
            ["metallic"] = new JsonArray(Colors.Where(c => c.Metallic).Select(c => (JsonNode)c.Name).ToArray()),
            ["random_words"] = new JsonArray(RandomWords.Select(w => (JsonNode)w).ToArray()),
            ["color_tint"] = Style.Tint,
            ["metallic_shine"] = Style.MetallicShine,
            ["animate_metallic"] = Style.AnimateMetallic,
            ["random_style"] = Style.RandomStyle,
            ["random_min_brightness"] = Style.RandomMinBrightness,
            ["animate_random"] = Style.AnimateRandom,
        };
        if (shopAccents is { Count: > 0 })
            json["shop_accents"] = new JsonObject(shopAccents.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
        if (css is not null) json["css"] = css;
        json["version"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json.ToJsonString())))[..12];
        return json;
    }
}

public sealed partial class OrderColorService(ISettingsStore settings)
{
    public const string SettingKey = "Orders.ColorConfig";

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexPattern();

    public const string ExampleHex = "#B87333";

    public static bool IsHex(string value) => HexPattern().IsMatch(value.Trim());

    /// <summary>The saved list, or the default one (the colors the HA dashboard card had) until it is first saved.</summary>
    public async Task<OrderColorConfig> GetAsync(CancellationToken ct = default) =>
        await settings.GetAsync(SettingKey, ct) is { Length: > 0 } json ? Parse(json) : Default;

    public async Task SaveAsync(OrderColorConfig config, string? userName, CancellationToken ct = default)
    {
        var problems = Validate(config);
        if (problems.Count > 0) throw new ArgumentException(string.Join(" ", problems));
        var normalized = Normalize(config);
        await settings.SetAsync(SettingKey, normalized.ToCardJson().ToJsonString(), updatedBy: userName, ct: ct);
    }

    /// <summary>What's wrong with a list, in words for the settings page. Empty when it can be saved.</summary>
    public static List<string> Validate(OrderColorConfig config)
    {
        var problems = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in config.Colors)
        {
            var name = c.Name.Trim();
            if (name.Length == 0) { problems.Add("Every color needs a name."); continue; }
            if (!seen.Add(name)) problems.Add($"\"{name}\" is listed twice.");
            if (c.Hex.Count == 0) problems.Add($"\"{name}\" needs at least one color.");
            foreach (var h in c.Hex.Where(h => !IsHex(h)))
                problems.Add($"\"{name}\": \"{h}\" isn't a color like {ExampleHex}.");
        }
        var look = config.Style;
        if (look.Tint is < 0 or > 100) problems.Add("Tint strength must be 0 to 100.");
        if (look.MetallicShine is < 0 or > 100) problems.Add("Metal shine must be 0 to 100.");
        if (look.RandomMinBrightness is < 0 or > 100) problems.Add("Random color brightness must be 0 to 100.");
        if (look.RandomStyle is not (OrderColorLook.Lava or OrderColorLook.Confetti)) problems.Add("The random effect must be lava or confetti.");
        return problems;
    }

    /// <summary>Parses the card's JSON shape: colors as "#hex" or ["#hex", …], metallic as names.</summary>
    public static OrderColorConfig Parse(string json)
    {
        var root = JsonNode.Parse(json)!.AsObject();
        var metallic = new HashSet<string>(
            root["metallic"]?.AsArray().Select(n => n!.GetValue<string>().Trim()) ?? [], StringComparer.OrdinalIgnoreCase);
        var colors = (root["colors"]?.AsObject() ?? [])
            .Select(kv => new OrderColor(kv.Key.Trim(),
                kv.Value is JsonArray a ? a.Select(n => n!.GetValue<string>()).ToList() : [kv.Value!.GetValue<string>()],
                metallic.Contains(kv.Key.Trim())))
            .ToList();
        var randomWords = root["random_words"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? ["random"];
        var d = OrderColorLook.Default;
        var look = new OrderColorLook(
            (int?)root["color_tint"] ?? d.Tint,
            (int?)root["metallic_shine"] ?? d.MetallicShine,
            (bool?)root["animate_metallic"] ?? d.AnimateMetallic,
            (string?)root["random_style"] ?? d.RandomStyle,
            (int?)root["random_min_brightness"] ?? d.RandomMinBrightness,
            (bool?)root["animate_random"] ?? d.AnimateRandom);
        return new OrderColorConfig(colors, randomWords, look);
    }

    private static OrderColorConfig Normalize(OrderColorConfig config) => new(
        config.Colors
            .Select(c => new OrderColor(c.Name.Trim(), c.Hex.Select(h => h.Trim().ToUpperInvariant()).ToList(), c.Metallic))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        config.RandomWords.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        config.Style);

    public static OrderColorConfig Default { get; } = LoadDefault();

    private static OrderColorConfig LoadDefault()
    {
        using var stream = typeof(OrderColorService).Assembly.GetManifestResourceStream("OrderColors.default.json")
                           ?? throw new InvalidOperationException("Embedded OrderColors.default.json is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
