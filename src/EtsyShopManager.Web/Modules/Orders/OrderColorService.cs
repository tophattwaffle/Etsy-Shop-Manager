using System.Security.Cryptography;
using System.Text;

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using EtsyShopManager.Core.Services;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>One named color: a filament or finish, with one or more hex colors (several for multi-color filaments).</summary>
public sealed record OrderColor(string Name, IReadOnlyList<string> Hex, bool Metallic);

/// <summary>
/// The order color list: which words in variations and personalization get tinted, and how. The app is the source of
/// truth; the Home Assistant card receives the same list through the integration (<see cref="ToCardJson"/>), and the
/// Orders page colors with the card's own engine, so both always agree.
/// </summary>
public sealed record OrderColorConfig(IReadOnlyList<OrderColor> Colors, IReadOnlyList<string> RandomWords)
{
    /// <summary>
    /// The card's config keys (<c>colors</c>, <c>metallic</c>, <c>random_words</c>), the shops' accent colors by Etsy
    /// shop id (<c>shop_accents</c>, when given) and a <c>version</c> hash of all of it.
    /// </summary>
    public JsonObject ToCardJson(IReadOnlyDictionary<string, string>? shopAccents = null)
    {
        var colors = new JsonObject();
        foreach (var c in Colors) colors[c.Name] = new JsonArray(c.Hex.Select(h => (JsonNode)h).ToArray());
        var json = new JsonObject
        {
            ["colors"] = colors,
            ["metallic"] = new JsonArray(Colors.Where(c => c.Metallic).Select(c => (JsonNode)c.Name).ToArray()),
            ["random_words"] = new JsonArray(RandomWords.Select(w => (JsonNode)w).ToArray()),
        };
        if (shopAccents is { Count: > 0 })
            json["shop_accents"] = new JsonObject(shopAccents.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
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
        return new OrderColorConfig(colors, randomWords);
    }

    private static OrderColorConfig Normalize(OrderColorConfig config) => new(
        config.Colors
            .Select(c => new OrderColor(c.Name.Trim(), c.Hex.Select(h => h.Trim().ToUpperInvariant()).ToList(), c.Metallic))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList(),
        config.RandomWords.Select(w => w.Trim()).Where(w => w.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList());

    public static OrderColorConfig Default { get; } = LoadDefault();

    private static OrderColorConfig LoadDefault()
    {
        using var stream = typeof(OrderColorService).Assembly.GetManifestResourceStream("OrderColors.default.json")
                           ?? throw new InvalidOperationException("Embedded OrderColors.default.json is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }
}
