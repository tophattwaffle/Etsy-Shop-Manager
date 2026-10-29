using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>How to draw one variation pill or personalization note: extra classes ("" when uncolored), its style and
/// the markup that goes before its text (the lava effect's blobs).</summary>
public sealed record OrderColorBox(string Cls, string Style, string Inner)
{
    public static readonly OrderColorBox None = new("", "", "");

    public bool IsColored => Style.Length > 0;

    public JsonObject ToJson() => new() { ["cls"] = Cls, ["style"] = Style, ["inner"] = Inner };
}

/// <summary>
/// The order coloring engine: tints variation pills and personalization notes by the colors they name, with metal and
/// "random" effects. The app is its only home: the Orders page draws with it, and the Home Assistant payload carries
/// each box already drawn (<see cref="Paint"/>) plus <see cref="Css"/>, so the card only paints what it is given.
/// Styles mix the tints with <c>var(--esm-color-base)</c>, the background each host sets.
/// </summary>
public sealed partial class OrderColorEngine
{
    public const string Base = "var(--esm-color-base)";

    private static readonly string[] Rainbow = ["#e53935", "#fb8c00", "#fdd835", "#43a047", "#1e88e5", "#8e24aa"];

    private readonly OrderColorLook _look;
    private readonly Dictionary<string, IReadOnlyList<string>> _colorMap = [];
    private readonly int _colorMaxWords;
    private readonly HashSet<string> _metallic;
    private readonly IReadOnlyList<string> _palette;
    private readonly List<string> _randomWords;

    public OrderColorEngine(OrderColorConfig config)
    {
        _look = config.Style;
        // Color names are matched word by word, so "Dark  Green!" finds the key "dark green".
        foreach (var c in config.Colors) _colorMap[string.Join(' ', Words(c.Name))] = c.Hex;
        _colorMaxWords = Math.Max(3, _colorMap.Keys.Select(k => k.Split(' ').Length).DefaultIfEmpty(0).Max());
        _metallic = config.Colors.Where(c => c.Metallic).Select(c => string.Join(' ', Words(c.Name))).Where(k => k.Length > 0).ToHashSet();
        // The random effects skip dark colors, dull greys and browns (both look muddy) and near-whites (which vanish on a
        // light background); vivid colors stay even when they read darkish, like a deep red.
        var minV = _look.RandomMinBrightness / 100.0;
        var palette = _colorMap.Values.SelectMany(v => v).Distinct().Where(col =>
        {
            var (v, s, luma) = Tone(col);
            return v >= minV && !(s < 0.3 && v < 0.8) && luma < 0.9;
        }).ToList();
        _palette = palette.Count > 0 ? palette : Rainbow;
        _randomWords = config.RandomWords.Select(w => string.Join(' ', Words(w))).Where(w => w.Length > 0).ToList();
    }

    /// <summary>How to draw a box of <paramref name="text"/>. A "random" choice gets the random effect, seeded by
    /// <paramref name="seed"/> so it stays put between renders; <paramref name="small"/> is a pill rather than a note.</summary>
    public OrderColorBox Box(string? text, string seed, bool small)
    {
        text ??= "";
        if (IsRandom(text) && RandomFx($"{seed}|{text}", small) is { } fx)
            return new($" colored random {fx.Kind}{(_look.AnimateRandom ? "" : " still")}", fx.Style, fx.Inner);
        var groups = ColorsIn(text);
        var metal = MetalStyle(groups);
        var style = ColorStyle(groups) + (metal.Length > 0 ? $" {metal}" : "");
        if (style.Length == 0) return OrderColorBox.None;
        var multi = groups.Any(g => g.Colors.Count > 1) ? " multi" : "";
        return new($" colored{multi}{(metal.Length > 0 ? $" metal{(_look.AnimateMetallic ? "" : " still")}" : "")}", style, "");
    }

    /// <summary>One named color found in text: its colors (several for a multi-color filament) and whether it is metallic.</summary>
    public sealed record Group(IReadOnlyList<string> Colors, bool Metal);

    /// <summary>
    /// Colors named in free text, in the order written: the color list first (longest name wins), then CSS color names,
    /// joining words ("Dark Green" → darkgreen). Each name is one group, so a multi-color filament stays together.
    /// </summary>
    public List<Group> ColorsIn(string text)
    {
        var w = Words(text);
        var found = new List<Group>();
        for (var i = 0; i < w.Count;)
        {
            var used = 1;
            for (var n = Math.Min(_colorMaxWords, w.Count - i); n > 0; n--)
            {
                var parts = w.GetRange(i, n);
                var key = string.Join(' ', parts);
                var joined = string.Concat(parts);
                var cols = _colorMap.GetValueOrDefault(key) ?? (IsCssColor(joined) ? [joined] : null);
                if (cols is null) continue;
                if (found.Count == 0 || string.Join(',', found[^1].Colors) != string.Join(',', cols))
                    found.Add(new Group(cols, _metallic.Contains(key)));
                used = n;
                break;
            }
            i += used;
        }
        return found;
    }

    // A tinted fill fading through the colors, with a full-strength outline in the same colors. Each named color gets an
    // equal share of the width, with its stop at the middle of its share so neighbours fade into each other. A multi-color
    // filament stacks its colors top to bottom over its share instead (MultiStyle). The fill is mixed with the background
    // so it stays opaque over the outline layer.
    private string ColorStyle(List<Group> groups)
    {
        int GroupTint(Group g) => g.Metal ? _look.EffectiveMetallicTint : _look.Tint;
        if (groups.Count == 0 || !groups.Any(g => GroupTint(g) > 0)) return "";
        var stops = groups.Select((g, gi) => (
            Col: g.Colors.Count > 1 ? $"color-mix(in srgb, {g.Colors[0]}, {g.Colors[^1]})" : g.Colors[0],
            Tint: GroupTint(g),
            P: (gi + 0.5) / groups.Count * 100)).ToList();
        if (stops.Count == 1) stops = [(stops[0].Col, stops[0].Tint, 0), (stops[0].Col, stops[0].Tint, 100)];
        string Tint(string col, int t) => $"color-mix(in srgb, {col} {N(Math.Min(100, Math.Max(0, t)))}%, {Base})";
        string Grad(Func<(string Col, int Tint, double P), string> fn) =>
            $"linear-gradient(90deg, {string.Join(", ", stops.Select(s => $"{fn(s)} {F1(s.P)}%"))})";
        return $"background: {Grad(s => Tint(s.Col, s.Tint))} padding-box, {Grad(s => s.Col)} border-box; border-color: transparent;{MultiStyle(groups, Tint)}";
    }

    // The stacked layer (.multi::before) for multi-color filaments: each one's colors run top to bottom over its share of
    // the box, tinted inside and full strength on the outline. "" when no group has more than one color. Where two
    // filaments meet ("one in Nebula, one in Sunrise") the layer fades out too, so the left-to-right blend joins them
    // rather than a hard seam.
    private string MultiStyle(List<Group> groups, Func<string, int, string> tint)
    {
        if (!groups.Any(g => g.Colors.Count > 1)) return "";
        var share = 100.0 / groups.Count;
        // Background-position percentages are of (box - layer), so this puts the layer's left edge at gi * share.
        string At(int gi) => $"{F1(share >= 100 ? 0 : gi * share / (100 - share) * 100)}% 0 / {F1(share)}% 100% no-repeat";
        var multis = groups.Select((g, gi) => (g, gi)).Where(x => x.g.Colors.Count > 1).ToList();
        IEnumerable<string> Layer(Func<Group, string, string> fn, string box) =>
            multis.Select(m => $"linear-gradient(180deg, {string.Join(", ", m.g.Colors.Select(c => fn(m.g, c)))}) {At(m.gi)} {box}");
        return $" --multi: {string.Join(", ", Layer((g, c) => tint(c, g.Metal ? _look.EffectiveMetallicTint : _look.Tint), "padding-box").Concat(Layer((_, c) => c, "border-box")))}; --multi-mask: {(groups.Count == 1 ? "none" : ShareMask(groups, g => g.Colors.Count > 1, fadeBetween: true))};";
    }

    // A left-to-right mask, opaque over the shares of the groups `pick` selects and transparent elsewhere, fading where a
    // picked share meets another color (not at the box's own edges); "none" when every group is picked, unless
    // `fadeBetween` asks for a fade between neighbouring picked shares as well.
    private static string ShareMask(List<Group> groups, Func<Group, bool> pick, bool fadeBetween = false)
    {
        if (!fadeBetween && groups.All(pick)) return "none";
        var share = 100.0 / groups.Count;
        var fade = share / 4;
        var stops = groups.SelectMany((g, gi) =>
        {
            if (!pick(g)) return Enumerable.Empty<string>();
            var (a, b) = (gi * share, (gi + 1) * share);
            string[] from = a > 0 ? [$"transparent {F1(a)}%", $"#000 {F1(a + fade)}%"] : ["#000 0%"];
            string[] to = b < 100 ? [$"#000 {F1(b - fade)}%", $"transparent {F1(b)}%"] : ["#000 100%"];
            return from.Concat(to);
        });
        return $"linear-gradient(90deg, {string.Join(", ", stops)})";
    }

    // The metal layer (.metal::after) over the metallic groups' shares of the box, or "" when none: in "Black & Copper"
    // only the copper half shines; when every group is metallic the whole box does.
    private string MetalStyle(List<Group> groups)
    {
        var shine = _look.MetallicShine;
        if (!(shine > 0) || !(_look.EffectiveMetallicTint > 0) || !groups.Any(g => g.Metal)) return "";
        return $"--shine: {N(Math.Min(100, shine) / 100.0)}; --metal-mask: {ShareMask(groups, g => g.Metal)};";
    }

    public bool IsRandom(string text)
    {
        var s = $" {string.Join(' ', Words(text))} ";
        return _randomWords.Any(w => s.Contains($" {w} ", StringComparison.Ordinal));
    }

    private sealed record Fx(string Kind, string Style, string Inner);

    // A "random" box: colors drawn from the palette. Behind the effect is a slow drift through tinted palette colors, and
    // around it a multicolor outline. Both are horizontal tiles sized in pixels rather than box widths, so a small pill and
    // a wide note show the same colors per inch; each tile ends on its first color, so sliding it a whole tile loops
    // seamlessly. Null when coloring is off.
    private Fx? RandomFx(string seed, bool small)
    {
        var t = _look.EffectiveRandomTint;
        if (!(t > 0)) return null;
        var r = Rng(seed);
        string Pick() => _palette[(int)Math.Floor(r() * _palette.Count)];
        static string Loop(List<string> cols) => string.Join(", ", cols.Append(cols[0]));
        var wash = Enumerable.Range(0, 4).Select(_ => Pick()).ToList()
            .Select(col => $"color-mix(in srgb, {col} {N(Math.Min(100, t + 10))}%, {Base})").ToList();
        var ring = Enumerable.Range(0, 5).Select(_ => Pick()).ToList();
        var frame = $"background: linear-gradient(90deg, {Loop(wash)}) padding-box, linear-gradient(90deg, {Loop(ring)}) border-box; background-size: 480px 100%, 240px 100%; border-color: transparent;";

        if (_look.RandomStyle == OrderColorLook.Confetti)
        {
            // Dots go in --confetti for the .confetti::before layer, which tiles them vertically so they can fall in a
            // seamless loop (dots stay off the tile edges so none pop at the seam).
            var (dots, maxSize) = small ? (9, 1.8) : (27, 3.0);
            var strength = Math.Min(100, t + 40);
            var confetti = Enumerable.Range(0, dots).Select(_ =>
            {
                var size = maxSize / 2 + r() * maxSize / 2;
                var at = $"{JsRound(r() * 100)}% {JsRound(10 + r() * 80)}%";
                return $"radial-gradient(circle at {at}, color-mix(in srgb, {Pick()} {N(strength)}%, transparent) {F1(size)}px, transparent {F1(size + 0.8)}px)";
            }).ToList();
            return new("confetti", $"--confetti: {string.Join(", ", confetti)}; {frame}", "");
        }

        // Lava lamp: a few soft blobs spread across the box, each drifting and swelling on its own path and pace. The soft
        // edges come from the gradient itself, so the motion is transform-only.
        var n = small ? 3 : 4;
        var lavaStrength = Math.Min(100, t + 30);
        int[] durations = [10, 12, 15, 20];
        var blobs = Enumerable.Range(0, n).Select(k =>
        {
            var x = JsRound((k + 0.2 + r() * 0.6) / n * 100);
            var y = JsRound(25 + r() * 50);
            var d = durations[(int)Math.Floor(r() * 4)];
            var style = $"left: {x}%; top: {y}%; --c: color-mix(in srgb, {Pick()} {N(lavaStrength)}%, transparent); --d: {d}s; --k: lava-{k % 4 + 1}";
            return $"<i class=\"blob\" style=\"{Esc(style)}\"></i>";
        }).ToList();
        return new("lava", frame, string.Concat(blobs));
    }

    /// <summary>
    /// Adds the drawn boxes to a Home Assistant open-orders payload (<c>shops[].open_orders[].items[]</c>): each variation
    /// gets <c>paint</c> and an item with personalization gets <c>personalization_paint</c>, seeded by receipt and
    /// position as the card always did. Uncolored boxes get none.
    /// </summary>
    public void Paint(JsonObject payload)
    {
        foreach (var shop in payload["shops"]?.AsArray() ?? [])
        foreach (var order in shop?["open_orders"]?.AsArray() ?? [])
        {
            var receipt = (string?)order!["receipt_id"];
            var items = order["items"]?.AsArray() ?? [];
            for (var ii = 0; ii < items.Count; ii++)
            {
                var item = items[ii]!.AsObject();
                var variations = item["variations"]?.AsArray() ?? [];
                for (var vi = 0; vi < variations.Count; vi++)
                {
                    var v = variations[vi]!.AsObject();
                    var box = Box((string?)v["value"], $"{receipt}|{ii}.{vi}", small: true);
                    if (box.IsColored) v["paint"] = box.ToJson();
                }
                if ((string?)item["personalization"] is { Length: > 0 } note
                    && Box(note, $"{receipt}|{ii}.note", small: false) is { IsColored: true } noteBox)
                    item["personalization_paint"] = noteBox.ToJson();
            }
        }
    }

    // Brightness (HSV value: the brightest channel), saturation and perceived lightness, each 0–1, of a #rrggbb color.
    // Anything else passes as bright, vivid and mid-light.
    private static (double V, double S, double Luma) Tone(string col)
    {
        if (!HexColor().IsMatch(col)) return (1, 1, 0.5);
        var n = Convert.ToInt32(col[1..], 16);
        var (r, g, b) = ((n >> 16) / 255.0, ((n >> 8) & 255) / 255.0, (n & 255) / 255.0);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        return (max, max > 0 ? (max - min) / max : 0, 0.299 * r + 0.587 * g + 0.114 * b);
    }

    // Seeded PRNG, so a "random" box keeps the same pattern on every render (the card's FNV-1a seed and mixer).
    private static Func<double> Rng(string seed)
    {
        unchecked
        {
            var h = 2166136261u;
            for (var i = 0; i < seed.Length; i++)
            {
                // One step per code point, using its first UTF-16 unit.
                h = (h ^ seed[i]) * 16777619u;
                if (char.IsHighSurrogate(seed[i]) && i + 1 < seed.Length && char.IsLowSurrogate(seed[i + 1])) i++;
            }
            return () =>
            {
                h = (h ^ (h >> 15)) * 2246822507u;
                h = (h ^ (h >> 13)) * 3266489909u;
                h ^= h >> 16;
                return h / 4294967296.0;
            };
        }
    }

    private static List<string> Words(string? s) =>
        WordPattern().Matches((s ?? "").ToLowerInvariant()).Select(m => m.Value).ToList();

    private static bool IsCssColor(string w) => w.Length >= 3 && CssColorNames.Contains(w);

    private static string F1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    private static string N(double v) => v.ToString(CultureInfo.InvariantCulture);
    private static long JsRound(double v) => (long)Math.Floor(v + 0.5);

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;")
        .Replace("\"", "&quot;").Replace("'", "&#39;");

    [GeneratedRegex("[a-z]+")]
    private static partial Regex WordPattern();

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColor();

    /// <summary>CSS's named colors, recognised in text without being on the list ("dark green" → darkgreen).</summary>
    private static readonly HashSet<string> CssColorNames =
    [
        "aliceblue", "antiquewhite", "aqua", "aquamarine", "azure", "beige", "bisque", "black", "blanchedalmond", "blue",
        "blueviolet", "brown", "burlywood", "cadetblue", "chartreuse", "chocolate", "coral", "cornflowerblue", "cornsilk",
        "crimson", "cyan", "darkblue", "darkcyan", "darkgoldenrod", "darkgray", "darkgreen", "darkgrey", "darkkhaki",
        "darkmagenta", "darkolivegreen", "darkorange", "darkorchid", "darkred", "darksalmon", "darkseagreen",
        "darkslateblue", "darkslategray", "darkslategrey", "darkturquoise", "darkviolet", "deeppink", "deepskyblue",
        "dimgray", "dimgrey", "dodgerblue", "firebrick", "floralwhite", "forestgreen", "fuchsia", "gainsboro",
        "ghostwhite", "gold", "goldenrod", "gray", "green", "greenyellow", "grey", "honeydew", "hotpink", "indianred",
        "indigo", "ivory", "khaki", "lavender", "lavenderblush", "lawngreen", "lemonchiffon", "lightblue", "lightcoral",
        "lightcyan", "lightgoldenrodyellow", "lightgray", "lightgreen", "lightgrey", "lightpink", "lightsalmon",
        "lightseagreen", "lightskyblue", "lightslategray", "lightslategrey", "lightsteelblue", "lightyellow", "lime",
        "limegreen", "linen", "magenta", "maroon", "mediumaquamarine", "mediumblue", "mediumorchid", "mediumpurple",
        "mediumseagreen", "mediumslateblue", "mediumspringgreen", "mediumturquoise", "mediumvioletred", "midnightblue",
        "mintcream", "mistyrose", "moccasin", "navajowhite", "navy", "oldlace", "olive", "olivedrab", "orange",
        "orangered", "orchid", "palegoldenrod", "palegreen", "paleturquoise", "palevioletred", "papayawhip", "peachpuff",
        "peru", "pink", "plum", "powderblue", "purple", "rebeccapurple", "red", "rosybrown", "royalblue", "saddlebrown",
        "salmon", "sandybrown", "seagreen", "seashell", "sienna", "silver", "skyblue", "slateblue", "slategray",
        "slategrey", "snow", "springgreen", "steelblue", "tan", "teal", "thistle", "tomato", "turquoise", "violet",
        "wheat", "white", "whitesmoke", "yellow", "yellowgreen",
    ];

    /// <summary>
    /// The styles behind .colored, .random and .metal boxes (pills use .var, notes .note). The app links it from
    /// <see cref="CssUrl"/>; the HA card gets it in <c>color_config.css</c>. Animations use var(--phase) so a re-render
    /// continues them where they were.
    /// </summary>
    public const string Css = """
        .var.colored { color: var(--primary-text-color); font-weight: 500; }
        .note.colored { border-width: 1px 1px 1px 4px; border-style: solid; }
        /* Multi-color filaments: their colors stacked top to bottom (--multi), over the base blend and under the
           text, covering the outline too; --multi-mask limits them to their share of the box. */
        .multi { position: relative; isolation: isolate; }
        .multi::before { content: ""; position: absolute; inset: -1px; z-index: -1; pointer-events: none; border-radius: inherit;
                         border: 1px solid transparent; background: var(--multi);
                         -webkit-mask-image: var(--multi-mask); mask-image: var(--multi-mask); }
        .note.multi::before { inset: -1px -1px -1px -4px; border-width: 1px 1px 1px 4px; }
        .random { position: relative; overflow: hidden; isolation: isolate; animation: random-drift 48s linear infinite var(--phase, 0ms); }
        .confetti::before { content: ""; position: absolute; left: 0; right: 0; top: -100%; height: 200%; z-index: -1; pointer-events: none;
                            background: var(--confetti); background-size: 100% 50%; animation: confetti-fall 4s linear infinite var(--phase, 0ms); }
        .blob { position: absolute; z-index: -1; pointer-events: none; width: 70%; height: 300%; translate: -50% -50%; border-radius: 50%;
                background: radial-gradient(closest-side, var(--c) 25%, transparent);
                animation: var(--k) var(--d) ease-in-out infinite alternate var(--phase, 0ms); }
        .random.still, .random.still::before, .random.still .blob { animation: none; }
        @keyframes confetti-fall { to { transform: translateY(50%); } }
        /* Per cycle the background moves one 480px tile and the outline two 240px tiles. */
        @keyframes random-drift { to { background-position: 480px 0, 480px 0; } }
        @keyframes lava-1 { from { transform: translate(-40%, -15%) scale(0.9); } to { transform: translate(45%, 20%) scale(1.25); } }
        @keyframes lava-2 { from { transform: translate(35%, 20%) scale(1.2); } to { transform: translate(-45%, -20%) scale(0.85); } }
        @keyframes lava-3 { from { transform: translate(-20%, 25%) scale(1); } to { transform: translate(30%, -25%) scale(1.3); } }
        @keyframes lava-4 { from { transform: translate(25%, -25%) scale(1.15); } to { transform: translate(-35%, 15%) scale(0.9); } }
        /* Metal: a bevel (light top edge, darker band below the middle) and a glint that sweeps
           left to right in the first third of each cycle. The layer covers the border too and sits
           under the text; the glint is a 300%-wide tile, off the box at either end position. */
        .metal { position: relative; isolation: isolate; }
        .metal::after { content: ""; position: absolute; inset: -1px; z-index: -1; pointer-events: none; border-radius: inherit;
                        background: linear-gradient(105deg, transparent 43%, rgba(255, 255, 255, calc(var(--shine) * 0.95)) 50%, transparent 57%) 100% 0 / 300% 100% no-repeat,
                                    linear-gradient(180deg, rgba(255, 255, 255, calc(var(--shine) * 0.5)), rgba(255, 255, 255, 0) 42%,
                                                    rgba(0, 0, 0, calc(var(--shine) * 0.22)) 58%, rgba(255, 255, 255, calc(var(--shine) * 0.18)));
                        -webkit-mask-image: var(--metal-mask); mask-image: var(--metal-mask);
                        animation: metal-glint 6s ease-in-out infinite var(--phase, 0ms); }
        .note.metal::after { inset: -1px -1px -1px -4px; }
        .metal.still::after { animation: none; }
        @keyframes metal-glint { 0% { background-position: 100% 0, 0 0; } 33%, 100% { background-position: 0% 0, 0 0; } }
        """;

    public const string CssUrl = "/css/order-colors.css";

    /// <summary>The CSS URL with a content hash, so browsers pick up a new version after each deploy.</summary>
    public static readonly string VersionedCssUrl = CssUrl.TrimStart('/') + "?v=" +
        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Css)))[..12];
}
