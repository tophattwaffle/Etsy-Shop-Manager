using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Components.Pages.Orders;
using EtsyShopManager.Web.Modules.Orders;

namespace EtsyShopManager.Tests;

/// <summary>Instructions linked to listings, printed on the packing and gift slips of their orders (made-up orders).</summary>
public class SlipDocumentTests
{
    // OrderPrintTests' sample orders: 1001 has listings 10 and 11, 1002 has listing 10 twice.
    private static async Task<(SlipDocumentService Service, int Care, int Video)> SeedAsync(TestDb db, bool firstIsGift)
    {
        await OrderPrintTests.SeedAsync(db, firstIsGift);
        await using (var ctx = db.CreateDbContext())
        {
            ctx.Listings.AddRange(
                new Listing { Id = 1, ShopId = 1, EtsyListingId = 10, Title = "Mini Caution Sign", State = "active" },
                new Listing { Id = 2, ShopId = 1, EtsyListingId = 11, Title = "Desk Cone", State = "active" },
                new Listing { Id = 3, ShopId = 1, EtsyListingId = 12, Title = "Unlinked thing", State = "active" });
            await ctx.SaveChangesAsync();
        }
        var service = new SlipDocumentService(db, new NullAuditLog(), TimeProvider.System);
        var (care, error) = await service.SaveAsync(new SlipDocument
        {
            Name = "Sign care", Heading = "Caring for your sign", Text = "Wipe with a damp cloth.\nKeep out of the sun.",
        }, "tester");
        Assert.Null(error);
        (var video, error) = await service.SaveAsync(new SlipDocument
        {
            Name = "Cone video", Heading = "Assembling your cone", Text = "Snap the base on first.",
            Url = "https://example.com/cone-video", QrCaption = "Watch how",
        }, "tester");
        Assert.Null(error);
        Assert.Null(await service.LinkAsync([1], care, "tester"));
        Assert.Null(await service.LinkAsync([2], video, "tester"));
        return (service, care, video);
    }

    /// <summary>The slip (a section) that prints the order.</summary>
    private static string SlipOf(string html, long receipt, string section = "<section class=\"slip\"")
    {
        var at = html.IndexOf($"#{receipt}", StringComparison.Ordinal);
        var from = html[html[..at].LastIndexOf(section, StringComparison.Ordinal)..];
        return from[..(from.IndexOf("</section>", StringComparison.Ordinal) + 10)];
    }

    private static int CountOf(string text, string part) => (text.Length - text.Replace(part, "").Length) / part.Length;

    [Fact]
    public async Task Instructions_for_an_orders_listings_print_once_each_inside_its_one_page_slip()
    {
        using var db = new TestDb();
        await SeedAsync(db, firstIsGift: false);

        var slips = await OrderPrintTests.RenderAsync<PrintPackingSlips>(db, new InMemorySettings(), "orders/print/slips?ids=1,2");
        Assert.Equal(2, CountOf(slips, "<section class=\"slip\""));

        // Both listings: both sets side by side inside the order's slip (so on its page), in item order, one QR code drawn.
        var first = SlipOf(slips, 1001);
        Assert.Contains("slip-docs slip-docs-many", first);
        Assert.Equal(2, CountOf(first, "class=\"slip-doc\""));
        Assert.True(first.IndexOf("Caring for your sign", StringComparison.Ordinal) < first.IndexOf("Assembling your cone", StringComparison.Ordinal));
        foreach (var text in new[] { "Wipe with a damp cloth.", "Keep out of the sun.", "Snap the base on first.", "Watch how" })
            Assert.Contains(text, first);
        Assert.Equal(1, CountOf(first, "class=\"slip-doc-code\""));
        Assert.Contains("<svg", first[first.IndexOf("slip-doc-code", StringComparison.Ordinal)..]);

        // The same listing on two items: its instructions print once.
        var second = SlipOf(slips, 1002);
        Assert.Equal(1, CountOf(second, "Caring for your sign"));
        Assert.DoesNotContain("slip-docs-many", second);
        Assert.DoesNotContain("Assembling your cone", second);
    }

    [Fact]
    public async Task Gift_slips_print_the_instructions_and_unlinked_or_deleted_ones_print_nowhere()
    {
        using var db = new TestDb();
        var (service, _, video) = await SeedAsync(db, firstIsGift: true);
        var settings = new InMemorySettings();

        var gift = SlipOf(await OrderPrintTests.RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips?ids=1"), 1001,
            "<section class=\"gift-slip\"");
        Assert.Equal(2, CountOf(gift, "class=\"gift-doc\""));
        Assert.Contains("Assembling your cone", gift);
        Assert.Contains("class=\"gift-doc-code\"", gift);
        Assert.DoesNotContain("$", gift); // still no prices on a gift slip

        Assert.Null(await service.LinkAsync([1], null, "tester"));
        await service.DeleteAsync(video, "tester");
        Assert.Empty(await service.LinkedListingsAsync(video));
        var none = await OrderPrintTests.RenderAsync<PrintPackingSlips>(db, settings, "orders/print/slips?ids=1,2");
        Assert.DoesNotContain("class=\"gift-doc\"", none);
        Assert.DoesNotContain("<footer class=\"slip-docs", none);
    }

    [Fact]
    public async Task Instructions_are_checked_before_saving_and_a_listing_prints_one_set()
    {
        using var db = new TestDb();
        var (service, care, video) = await SeedAsync(db, firstIsGift: false);

        Assert.Equal("Give the instructions a name.", (await service.SaveAsync(new SlipDocument { Text = "x" }, null)).Error);
        Assert.Contains("already", (await service.SaveAsync(new SlipDocument { Name = "sign CARE", Text = "x" }, null)).Error);
        Assert.Contains("https://", (await service.SaveAsync(new SlipDocument { Name = "Bad", Text = "x", Url = "javascript:alert(1)" }, null)).Error);
        Assert.Contains("https://", (await service.SaveAsync(new SlipDocument { Name = "Bad", Url = "example.com" }, null)).Error);
        Assert.Contains("one page", (await service.SaveAsync(
            new SlipDocument { Name = "Long", Text = new string('a', SlipDocumentService.MaxTextLength + 1) }, null)).Error);
        Assert.Contains("text or a link", (await service.SaveAsync(new SlipDocument { Name = "Empty", Text = "  " }, null)).Error);
        var (qrOnly, error) = await service.SaveAsync(new SlipDocument { Name = "QR only", Url = " https://example.com/x ", QrCaption = "Scan me" }, null);
        Assert.Null(error);
        Assert.Equal("https://example.com/x", (await service.GetAsync(qrOnly))!.Url);

        // Linking the sign to other instructions replaces its first ones.
        Assert.Null(await service.LinkAsync([1, 3], video, null));
        Assert.Equal(video, await service.GetLinkedAsync(1));
        Assert.Empty(await service.LinkedListingsAsync(care));
        Assert.Equal(["Desk Cone", "Mini Caution Sign", "Unlinked thing"], (await service.LinkedListingsAsync(video)).Select(l => l.Title));
        Assert.Equal(3, (await service.ListAsync()).Single(d => d.Id == video).Listings);
        Assert.Equal("Cone video", Assert.Single(await service.SearchListingsAsync("caution")).LinkedTo);
        Assert.Equal("Desk Cone", Assert.Single(await service.SearchListingsAsync("11")).Title);
        Assert.Empty(await service.SearchListingsAsync(" "));
        Assert.Contains("deleted", await service.LinkAsync([1], 999, null));
    }

    [Fact]
    public async Task The_layout_preview_shows_made_up_instructions_or_the_ones_being_written()
    {
        using var db = new TestDb();
        await SeedAsync(db, firstIsGift: false);
        var layouts = new SlipLayoutService(db, new InMemorySettings(), new NullAuditLog(), TimeProvider.System);
        var print = new OrderPrintService(db, new InMemorySettings(), new NullAuditLog());
        var builtIn = SlipLayoutService.BuiltIn(SlipKind.Packing);

        var sample = await layouts.PreviewAsync(SlipKind.Packing, builtIn.Html, builtIn.Css, 1, "http://localhost/", print);
        Assert.Contains(SlipModel.SampleDocument.Heading!, sample);
        var own = await layouts.PreviewAsync(SlipKind.Packing, builtIn.Html, builtIn.Css, 1, "http://localhost/", print,
            documents: [SlipDocumentText.Of(new SlipDocument { Heading = "Mine", Text = "Hello" })]);
        Assert.Contains("Mine", own);
        Assert.DoesNotContain(SlipModel.SampleDocument.Heading!, own);
    }
}
