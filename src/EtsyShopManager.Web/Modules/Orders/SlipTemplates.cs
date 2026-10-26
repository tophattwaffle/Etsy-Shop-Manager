using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Modules.Listings;
using Fluid;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

/// <summary>The shop as a slip layout sees it (<c>shop.name</c>, <c>shop.location</c>, …).</summary>
/// <param name="Address">The shop's Etsy address as Etsy prints it ("name.etsy.com").</param>
/// <param name="Sales">Etsy's count of the shop's sales; nil until the shop's details are read.</param>
/// <param name="Note">The shop's packing slip note ("A note from the shop").</param>
/// <param name="QrCode">A QR code to the shop's Etsy page, as SVG markup: print it with <c>{{ shop.qr_code | raw }}</c>.</param>
public sealed record SlipShop(string Name, string? Title, string Address, string? IconUrl, string? Location, bool StarSeller,
    int? Sales, string? Note, string QrCode)
{
    /// <summary>The shop's ships-from address, one line each (<c>shop.from_lines</c>), printed under From as on Etsy's slip.</summary>
    public IReadOnlyList<string> FromLines { get; init; } = [];
}

/// <summary>One line of a slip's totals ("Item total", "$51.96").</summary>
public sealed record SlipTotal(string Label, string Amount);

/// <summary>The order as a slip layout sees it (<c>order.receipt_id</c>, <c>order.address_lines</c>, …).</summary>
/// <param name="GiftMessage">The gift message as one paragraph (line breaks the buyer typed become spaces).</param>
/// <param name="GiftFrom">Who the gift is from: the receipt's gift_sender, else the buyer's first name.</param>
/// <param name="PhotoUrl">The first item's photo.</param>
/// <param name="AddressChanged">
/// True when the buyer asked for another address after checkout (Orders page): <c>ship_to_name</c> and <c>address_lines</c>
/// are the new address, <c>etsy_ship_to_name</c> and <c>etsy_address_lines</c> the one Etsy (and its shipping label) still has.
/// </param>
/// <param name="SellerNote">The seller's note on the order (Change order dialog), when it's set to print on slips; else nil.</param>
public sealed record SlipOrder(long ReceiptId, string OrderDate, string? ShipBy, string? BuyerName, string? ShipToName,
    IReadOnlyList<string> AddressLines, string? PaymentMethod, string? ShippingMethod, string? NoteFromBuyer, bool IsGift,
    string? GiftMessage, string? GiftFrom, int ItemCount, IReadOnlyList<SlipTotal> Totals, string OrderTotal, string? PhotoUrl,
    bool AddressChanged, string? EtsyShipToName, IReadOnlyList<string> EtsyAddressLines, string? SellerNote = null);

/// <summary>One item as a slip layout sees it (<c>item.title</c>, <c>item.price</c> as "2 x $12.99", …).</summary>
public sealed record SlipItem(string Title, IReadOnlyList<string> Variations, string? Personalization, int Quantity, string Price,
    string? ImageUrl);

/// <summary>
/// Instructions linked to a listing in the order (<c>doc.heading</c>, <c>doc.text</c>, …; <see cref="SlipDocument"/>).
/// </summary>
/// <param name="QrCode">A QR code to <paramref name="Url"/>, as SVG markup (<c>{{ doc.qr_code | raw }}</c>); nil without a link.</param>
public sealed record SlipDocumentText(string? Heading, string? Text, string? Url, string? QrCaption, string? QrCode)
{
    public static SlipDocumentText Of(SlipDocument doc) => new(
        string.IsNullOrWhiteSpace(doc.Heading) ? null : doc.Heading, string.IsNullOrWhiteSpace(doc.Text) ? null : doc.Text,
        string.IsNullOrWhiteSpace(doc.Url) ? null : doc.Url,
        string.IsNullOrWhiteSpace(doc.QrCaption) ? null : doc.QrCaption,
        string.IsNullOrWhiteSpace(doc.Url) ? null : OrderPrintService.QrSvg(doc.Url));
}

/// <summary>Everything a slip layout can print: <c>shop</c>, <c>order</c>, <c>items</c> and <c>documents</c>.</summary>
public sealed record SlipModel(SlipShop Shop, SlipOrder Order, IReadOnlyList<SlipItem> Items)
{
    /// <summary>
    /// The instructions linked to the order's listings (Packing slips page), each once, in the order of the items that
    /// brought them.
    /// </summary>
    public IReadOnlyList<SlipDocumentText> Documents { get; init; } = [];

    /// <summary>
    /// The instructions for an order's items, each once, in item order. <paramref name="byEtsyListing"/> maps an Etsy
    /// listing id to the instructions it prints (<see cref="SlipDocumentService.ForEtsyListingsAsync"/>).
    /// </summary>
    public static IReadOnlyList<SlipDocumentText> DocumentsFor(Order order, IReadOnlyDictionary<long, SlipDocument> byEtsyListing) =>
    [
        .. order.Items.OrderBy(i => i.EtsyTransactionId)
            .Select(i => i.EtsyListingId is { } id ? byEtsyListing.GetValueOrDefault(id) : null)
            .OfType<SlipDocument>().DistinctBy(d => d.Id).Select(SlipDocumentText.Of),
    ];

    /// <summary>Made-up instructions for previews of a layout.</summary>
    public static SlipDocumentText SampleDocument => SlipDocumentText.Of(new SlipDocument
    {
        Heading = "Caring for your item",
        Text = "Wipe clean with a soft, damp cloth. Keep it out of direct sunlight so the colors stay bright. Scan the code for a short video.",
        Url = "https://www.etsy.com", QrCaption = "Watch the video",
    });

    /// <summary>A shop's own slip settings (Packing slips page) that go on its slips.</summary>
    /// <param name="DefaultGiftMessage">Printed as the gift message when the buyer wrote none (Packing slips page).</param>
    /// <param name="FromLines">The ships-from address, one line each (Packing slips page).</param>
    public sealed record ShopSettings(string? Note, string? Location, bool StarSeller, string? DefaultGiftMessage = null,
        IReadOnlyList<string>? FromLines = null);

    /// <summary>The model for one stored order.</summary>
    public static SlipModel For(Order order, ShopSettings shop)
    {
        var address = OrderPrintService.Address(order);
        var etsyAddress = OrderPrintService.EtsyAddress(order);
        var details = OrderPrintService.Details(order);
        var items = order.Items.OrderBy(i => i.EtsyTransactionId).ToList();
        var message = order.GiftMessage is { Length: > 0 } m
            ? string.Join(" ", m.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            : null;
        return new SlipModel(
            ShopOf(order.Shop, shop),
            new SlipOrder(order.EtsyReceiptId, ListingFormat.Local(order.EtsyCreatedAtUtc, "MMM d, yyyy"),
                order.ShipByUtc is null ? null : ListingFormat.Local(order.ShipByUtc, "MMM d, yyyy"),
                Blank(order.BuyerProfileName) ?? Blank(order.BuyerName), address.Name, address.Lines, details.PaymentMethod, details.ShippingMethod,
                Blank(OrderOverrides.Note(order)), order.IsGift, Blank(message) ?? (order.IsGift ? Blank(shop.DefaultGiftMessage) : null),
                OrderPrintService.GiftSender(order),
                items.Sum(i => Math.Max(1, i.Quantity)), [.. details.Totals.Select(t => new SlipTotal(t.Label, t.Amount))],
                details.OrderTotal, items.Select(i => i.ImageUrl).FirstOrDefault(u => !string.IsNullOrEmpty(u)),
                order.AddressOverride is not null, etsyAddress.Name, etsyAddress.Lines,
                order.SellerNoteOnSlips ? Blank(order.SellerNote) : null),
            [.. items.Select(i =>
            {
                var (variations, personalization) = OrderPrintService.Split(i);
                return new SlipItem(i.Title ?? "", variations, personalization, Math.Max(1, i.Quantity), OrderPrintService.LinePrice(i),
                    Blank(i.ImageUrl));
            })]);
    }

    /// <summary>
    /// A made-up order for previewing a layout (no buyer's data), with the shop's real name, icon and slip settings.
    /// <paramref name="second"/> gives a different one, for the other half of a gift slip sheet, whose buyer wrote no gift
    /// message (so it shows the default one).
    /// </summary>
    public static SlipModel Sample(Shop shop, ShopSettings settings, SlipKind kind, bool second = false) => second
        ? new(
            ShopOf(shop, settings),
            new SlipOrder(1234567891, "Oct 17, 2026", "Oct 21, 2026", "Casey Example", kind == SlipKind.Gift ? "Jordan Example" : "Casey Example",
                ["22 Sample Avenue", "Riverside, CA 92501", "United States"], "Paid via Etsy Payments", "USPS Ground Advantage",
                null, kind == SlipKind.Gift, kind == SlipKind.Gift ? Blank(settings.DefaultGiftMessage) : null, // no message from the buyer
                kind == SlipKind.Gift ? "Casey" : null, 1, [new("Item total", "$18.50"), new("Shipping total", "$4.50")], "$23.00", null,
                false, null, []),
            [new("Sample mug | personalized gift", ["Color: White"], "Jordan", 1, "1 x $18.50", null)])
        : new(
        ShopOf(shop, settings),
        new SlipOrder(1234567890, "Oct 16, 2026", "Oct 20, 2026", "Alex Sample", kind == SlipKind.Gift ? "Sam Sample" : "Alex Sample",
            ["1 Example Street", "Apt 2", "Springfield, IL 62701", "United States"], "Paid via Etsy Payments", "USPS Ground Advantage",
            kind == SlipKind.Gift ? null : "Could you wrap it? Thank you!", kind == SlipKind.Gift,
            kind == SlipKind.Gift ? "Happy birthday! Love you lots." : null, kind == SlipKind.Gift ? "Alex" : null, 3,
            [new("Item total", "$37.97"), new("Shipping total", "$5.00")], "$42.97", null, false, null, [],
            kind == SlipKind.Gift ? null : "As you asked, I made the large one in blue."),
        [
            new("Sample item | desk decor | gift idea", ["Color: Blue", "Size: Large"], "Sam", 2, "2 x $12.99", null),
            new("Another sample item", ["Color: Yellow"], null, 1, "1 x $11.99", null),
        ]);

    private static SlipShop ShopOf(Shop shop, ShopSettings settings) => new(
        shop.Name, Blank(shop.Title), OrderPrintService.ShopAddress(shop),
        shop.IconUrl is { Length: > 0 } ? ShopIconCache.UrlFor(shop.Id) : null,
        Blank(settings.Location), settings.StarSeller, shop.TransactionSoldCount is > 0 ? shop.TransactionSoldCount : null,
        Blank(settings.Note), OrderPrintService.ShopQrSvg(shop)) { FromLines = settings.FromLines ?? [] };

    // Liquid treats "" as true, so missing text is nil and {% if order.ship_by %} works as it reads.
    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>One printed slip: its layout's scope key (<see cref="SlipLayoutService.ScopeKey"/>) and its HTML.</summary>
/// <param name="OwnSheet">A gift slip that needs its sheet's top half for itself (a changed address, which is cut away).</param>
public sealed record PrintedSlip(SlipKind Kind, string Key, string Html, bool OwnSheet = false);

/// <summary>
/// Puts slips on sheets of paper: a packing slip fills a sheet; a gift slip is half a sheet, so gift slips go two to a
/// sheet (10 orders with 2 gifts print on 9 sheets). The second gift joins the first one's sheet wherever it comes in the
/// order, so sheets keep the orders' order otherwise. A gift that needs a sheet of its own (<see cref="PrintedSlip.OwnSheet"/>)
/// isn't paired.
/// </summary>
public static class SlipSheets
{
    public static List<List<PrintedSlip>> Arrange(IEnumerable<PrintedSlip> slips)
    {
        var sheets = new List<List<PrintedSlip>>();
        List<PrintedSlip>? halfFull = null;
        foreach (var slip in slips)
        {
            if (slip.Kind != SlipKind.Gift || slip.OwnSheet)
            {
                sheets.Add([slip]);
                continue;
            }
            if (halfFull is not null)
            {
                halfFull.Add(slip);
                halfFull = null;
                continue;
            }
            halfFull = [slip];
            sheets.Add(halfFull);
        }
        return sheets;
    }

    /// <summary>The sheets as HTML: each a .slip-sheet (gift sheets also .slip-sheet-gift) holding its slips' .slip-page.</summary>
    public static string Html(IEnumerable<List<PrintedSlip>> sheets)
    {
        var html = new System.Text.StringBuilder();
        foreach (var sheet in sheets)
        {
            html.Append(sheet[0].Kind == SlipKind.Gift ? "<div class=\"slip-sheet slip-sheet-gift\">" : "<div class=\"slip-sheet\">");
            foreach (var slip in sheet)
                html.Append("<div class=\"slip-page slip-layout-").Append(slip.Key).Append("\">").Append(slip.Html).Append("</div>");
            html.Append("</div>");
        }
        return html.ToString();
    }
}

/// <summary>
/// Renders slip layouts: Liquid templates (Fluid) over a <see cref="SlipModel"/>, values HTML-encoded unless a template
/// asks for <c>| raw</c>. Templates run sandboxed: they can only read the model, never call into the app.
/// </summary>
public static class SlipRenderer
{
    private static readonly FluidParser Parser = new();
    private static readonly TemplateOptions Options = CreateOptions();
    private static readonly ConcurrentDictionary<string, IFluidTemplate> Cache = new();

    private static TemplateOptions CreateOptions()
    {
        var options = new TemplateOptions { MaxSteps = 200_000, MaxRecursion = 20 };
        options.MemberAccessStrategy.MemberNameStrategy = MemberNameStrategies.SnakeCase;
        options.MemberAccessStrategy.Register<SlipModel>();
        options.MemberAccessStrategy.Register<SlipShop>();
        options.MemberAccessStrategy.Register<SlipOrder>();
        options.MemberAccessStrategy.Register<SlipItem>();
        options.MemberAccessStrategy.Register<SlipTotal>();
        options.MemberAccessStrategy.Register<SlipDocumentText>();
        return options;
    }

    /// <summary>Null when the template is valid Liquid, otherwise what's wrong with it (with its line and column).</summary>
    public static string? Check(string html) => Parser.TryParse(html, out _, out var error) ? null : error;

    /// <summary>
    /// The layout's HTML for one slip. A template that can't be parsed or fails while printing gives a short message in
    /// its place, so one broken layout never stops the other slips from printing.
    /// </summary>
    public static async Task<string> RenderAsync(string html, SlipModel model, bool cache = true)
    {
        IFluidTemplate? template;
        string? error = null;
        if (cache) template = Cache.GetOrAdd(html, h => Parser.TryParse(h, out var t, out _) ? t : null!);
        else Parser.TryParse(html, out template, out error);
        if (template is null)
            return Problem(error ?? Check(html) ?? "it can't be read");

        try
        {
            var context = new TemplateContext(model, Options);
            return await template.RenderAsync(context, HtmlEncoder.Default);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Problem(ex.Message);
        }
    }

    private static string Problem(string message) =>
        $"<div class=\"slip-problem\">This packing slip layout couldn't be printed: {HtmlEncoder.Default.Encode(message)}</div>";

    /// <summary>
    /// The layout's CSS limited to its own slips (CSS nesting under <c>.slip-layout-{key}</c>), safe to put in a style tag.
    /// </summary>
    public static string ScopedCss(string key, string css) =>
        $".slip-layout-{key} {{\n{css.Replace("</", "<\\/")}\n}}";
}

/// <summary>
/// Packing slip layouts (Packing slips page): the built-in packing and gift slips, the layouts saved from them, and which
/// layout each shop prints. A shop prints the built-in layout of a kind until it is given another.
/// </summary>
public sealed class SlipLayoutService(IDbContextFactory<AppDbContext> dbFactory, ISettingsStore settings, IAuditLog audit,
    TimeProvider time)
{
    public const int MaxNameLength = 100;

    private static readonly Dictionary<SlipKind, SlipLayout> BuiltIns = new()
    {
        [SlipKind.Packing] = new() { Name = "Built-in packing slip", Kind = SlipKind.Packing, Html = Resource("packing.liquid"), Css = Resource("packing.css") },
        [SlipKind.Gift] = new() { Name = "Built-in gift slip", Kind = SlipKind.Gift, Html = Resource("gift.liquid"), Css = Resource("gift.css") },
    };

    private static string Resource(string name)
    {
        using var stream = typeof(SlipLayoutService).Assembly.GetManifestResourceStream("Slips." + name)
                           ?? throw new InvalidOperationException($"Missing embedded slip layout {name}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>The built-in layout of a kind (id 0, read-only): what a shop prints until it is given another.</summary>
    public static SlipLayout BuiltIn(SlipKind kind)
    {
        var b = BuiltIns[kind];
        return new SlipLayout { Name = b.Name, Kind = b.Kind, Html = b.Html, Css = b.Css };
    }

    /// <summary>The key a layout's slips are scoped by in the printout ("builtin-gift", "7").</summary>
    public static string ScopeKey(SlipLayout layout) => layout.Id == 0 ? $"builtin-{layout.Kind.ToString().ToLowerInvariant()}" : layout.Id.ToString();

    public static string AssignmentKey(int shopId, SlipKind kind) => $"Shops.SlipLayout.{shopId}.{kind}";

    public async Task<List<SlipLayout>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SlipLayouts.AsNoTracking().OrderBy(l => l.Kind).ThenBy(l => l.Name).ToListAsync(ct);
    }

    public async Task<SlipLayout?> GetAsync(int id, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.SlipLayouts.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct);
    }

    /// <summary>
    /// Saves a new layout (Id 0) or changes to one. Returns what's wrong instead when the name is missing or taken or the
    /// template isn't valid Liquid; the saved layout's id otherwise.
    /// </summary>
    public async Task<(int Id, string? Error)> SaveAsync(SlipLayout layout, string? user, CancellationToken ct = default)
    {
        var name = layout.Name.Trim();
        if (name.Length == 0) return (0, "Give the layout a name.");
        if (name.Length > MaxNameLength) return (0, $"Keep the name to {MaxNameLength} characters.");
        if (SlipRenderer.Check(layout.Html) is { } problem) return (0, $"The HTML template has a problem: {problem}");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.SlipLayouts.AnyAsync(l => l.Id != layout.Id && l.Name == name, ct))
            return (0, $"There is already a layout called {name}.");

        var row = layout.Id == 0 ? null : await db.SlipLayouts.FirstOrDefaultAsync(l => l.Id == layout.Id, ct);
        if (layout.Id != 0 && row is null) return (0, "That layout was deleted.");
        if (row is null) db.SlipLayouts.Add(row = new SlipLayout { Kind = layout.Kind });
        row.Name = name;
        row.Html = layout.Html;
        row.Css = layout.Css;
        row.UpdatedAtUtc = time.GetUtcNow().UtcDateTime;
        row.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
        await audit.WriteAsync(layout.Id == 0 ? "slip-layout.created" : "slip-layout.updated", nameof(SlipLayout), row.Id.ToString(), name, user, ct);
        return (row.Id, null);
    }

    /// <summary>Deletes a layout; shops that printed it go back to the built-in layout.</summary>
    public async Task DeleteAsync(int id, IEnumerable<int> shopIds, string? user, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var row = await db.SlipLayouts.FirstOrDefaultAsync(l => l.Id == id, ct);
            if (row is null) return;
            db.SlipLayouts.Remove(row);
            await db.SaveChangesAsync(ct);
            await audit.WriteAsync("slip-layout.deleted", nameof(SlipLayout), id.ToString(), row.Name, user, ct);
        }
        foreach (var shopId in shopIds)
            foreach (var kind in Enum.GetValues<SlipKind>())
                if (await settings.GetAsync(AssignmentKey(shopId, kind), ct) == id.ToString())
                    await settings.SetAsync(AssignmentKey(shopId, kind), null, updatedBy: user, ct: ct);
    }

    /// <summary>The layout id each shop prints for each kind (0 = built-in).</summary>
    public async Task<Dictionary<(int ShopId, SlipKind Kind), int>> GetAssignmentsAsync(IEnumerable<int> shopIds, CancellationToken ct = default)
    {
        var result = new Dictionary<(int, SlipKind), int>();
        foreach (var shopId in shopIds.Distinct())
            foreach (var kind in Enum.GetValues<SlipKind>())
                result[(shopId, kind)] = int.TryParse(await settings.GetAsync(AssignmentKey(shopId, kind), ct), out var id) ? id : 0;
        return result;
    }

    /// <summary>Sets the layout a shop prints for a kind (0 = built-in). Returns what's wrong instead, if anything.</summary>
    public async Task<string?> AssignAsync(int shopId, SlipKind kind, int layoutId, string? user, CancellationToken ct = default)
    {
        if (layoutId != 0)
        {
            var layout = await GetAsync(layoutId, ct);
            if (layout is null) return "That layout was deleted.";
            if (layout.Kind != kind) return $"{layout.Name} is a {Describe(layout.Kind)} layout.";
        }
        await settings.SetAsync(AssignmentKey(shopId, kind), layoutId == 0 ? null : layoutId.ToString(), updatedBy: user, ct: ct);
        await audit.WriteAsync("shop.slip-layout", nameof(Shop), shopId.ToString(), $"{kind}: {(layoutId == 0 ? "built-in" : layoutId)}", user, ct);
        return null;
    }

    /// <summary>The layout each shop prints for each kind, for a printout (the built-in one where none is set or it's gone).</summary>
    public async Task<Dictionary<(int ShopId, SlipKind Kind), SlipLayout>> ResolveAsync(IEnumerable<int> shopIds, CancellationToken ct = default)
    {
        var assigned = await GetAssignmentsAsync(shopIds, ct);
        var ids = assigned.Values.Where(id => id != 0).Distinct().ToList();
        Dictionary<int, SlipLayout> layouts;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            layouts = await db.SlipLayouts.AsNoTracking().Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, ct);
        return assigned.ToDictionary(a => a.Key,
            a => layouts.TryGetValue(a.Value, out var l) && l.Kind == a.Key.Kind ? l : BuiltIn(a.Key.Kind));
    }

    public static string Describe(SlipKind kind) => kind == SlipKind.Gift ? "gift slip" : "packing slip";

    /// <summary>
    /// A whole page showing a layout as it would print for <paramref name="shopId"/> (its name, icon and slip settings)
    /// with a made-up order, for the editor's preview frame. Scaled down to fit beside the editor.
    /// </summary>
    /// <param name="documents">The instructions on the first slip (made-up ones when null).</param>
    public async Task<string> PreviewAsync(SlipKind kind, string html, string css, int? shopId, string baseUri, OrderPrintService print,
        CancellationToken ct = default, IReadOnlyList<SlipDocumentText>? documents = null)
    {
        Shop? shop;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            shop = await db.Shops.AsNoTracking().Where(s => shopId == null || s.Id == shopId).OrderBy(s => s.Name).FirstOrDefaultAsync(ct);
        shop ??= new Shop { Name = "SampleShop", Title = "Little things, made well", TransactionSoldCount = 1234 };
        var settings = shop.Id == 0
            ? new SlipModel.ShopSettings("Thanks for your order!", "Springfield, Illinois", true, OrderPrintService.DefaultGiftMessageText,
                ["SampleShop", "1 Maker Lane", "SPRINGFIELD, IL 62701", "United States"])
            : (await print.GetShopSettingsAsync([shop.Id], ct))[shop.Id];

        // A gift slip is half a sheet: show a whole sheet with two made-up gift orders, as they print.
        var slips = new List<PrintedSlip>
        {
            new(kind, "preview", await SlipRenderer.RenderAsync(html,
                SlipModel.Sample(shop, settings, kind) with { Documents = documents ?? [SlipModel.SampleDocument] }, cache: false)),
        };
        if (kind == SlipKind.Gift)
            slips.Add(new(kind, "preview", await SlipRenderer.RenderAsync(html, SlipModel.Sample(shop, settings, kind, second: true), cache: false)));
        var body = SlipSheets.Html(SlipSheets.Arrange(slips));
        var encoder = HtmlEncoder.Default;
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><base href="{{encoder.Encode(baseUri)}}">
            <link rel="stylesheet" href="print.css"><style>{{SlipRenderer.ScopedCss("preview", css)}}</style>
            <style>.print-sheet { zoom: 0.62; }</style></head>
            <body><div class="print-sheet">{{body}}</div></body></html>
            """;
    }
}
