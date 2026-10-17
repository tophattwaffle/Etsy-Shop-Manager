using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class TextBlockTests
{
    private const string Care = "Care: PLA softens above 50 °C.\nKeep it out of hot cars.";

    /// <summary>Listings 1–3 with the care text pasted into their descriptions (Etsy's \r\n newlines), listing 4 without it.</summary>
    private static async Task<(FakeEtsyShop Shop, Dictionary<long, int> Ids)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        foreach (var id in new long[] { 1, 2, 3 })
        {
            var listing = FakeEtsyShop.Listing(id, $"Vase {id}");
            listing["description"] = $"A lovely vase.\r\n\r\n{Care.Replace("\n", "\r\n")}\r\n\r\nThanks for looking!";
            shop.Listings.Add(listing);
        }
        shop.Listings.Add(FakeEtsyShop.Listing(4, "Plain vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, await db.Listings.ToDictionaryAsync(l => l.EtsyListingId, l => l.Id));
    }

    private static TextBlockService Blocks(FakeEtsyShop s) => s.Provider.GetRequiredService<TextBlockService>();
    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<string> DescriptionAsync(FakeEtsyShop s, int id) =>
        (string)(await Editor(s).LoadAsync(id))!.Model.Local("description")!;

    private static async Task RunPushAsync(FakeEtsyShop s, int pushId) =>
        await s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = pushId, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    [Fact]
    public async Task A_new_block_finds_the_listings_that_already_contain_it()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;
        var (blockId, error) = await Blocks(shop).SaveAsync(null, " PLA care ", Care.Replace("\n", "\r\n") + "  \r\n\r\n", "tophattwaffle");
        Assert.Null(error);
        Assert.Equal(Care, (await Blocks(shop).GetAsync(blockId!.Value))!.Text); // normalized: \n, trailing spaces and blank lines dropped

        var candidates = await Blocks(shop).CandidatesAsync(blockId.Value);
        Assert.Equal([ids[1], ids[2], ids[3]], candidates.Select(c => c.ListingId).Order());
        Assert.Equal(3, await Blocks(shop).LinkAsync(blockId.Value, [ids[1], ids[2], ids[3], ids[4]], "tophattwaffle")); // 4 lacks the text
        Assert.Empty(await Blocks(shop).CandidatesAsync(blockId.Value));
        Assert.All(await Blocks(shop).UsesAsync(blockId.Value), u => Assert.Equal(TextBlockUseStatus.UpToDate, u.Status));

        Assert.Equal("There's already a block called \"pla CARE\".", (await Blocks(shop).SaveAsync(null, "pla CARE", "x", "tophattwaffle")).Error);
        Assert.Equal("The block needs some text.", (await Blocks(shop).SaveAsync(null, "Other", " \r\n ", "tophattwaffle")).Error);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Editing_a_block_updates_linked_drafts_skips_hand_edits_and_pushes_through_the_reviewed_push()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;
        var (blockId, _) = await Blocks(shop).SaveAsync(null, "PLA care", Care, "tophattwaffle");
        await Blocks(shop).LinkAsync(blockId!.Value, [ids[1], ids[2], ids[3]], "tophattwaffle");

        // Listing 2's copy is edited by hand; listing 3's description is changed on etsy.com.
        await Editor(shop).SaveAsync(ids[2], new Dictionary<string, JsonNode?> { ["description"] = "Care: keep it cool." }, "tophattwaffle");
        shop.ListingJson(3)["description"] = "Rewritten on Etsy";
        shop.ListingJson(3)["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        const string newCare = "Care: PLA softens above 50 °C.\nKeep it out of hot cars and direct sun.";
        await Blocks(shop).SaveAsync(blockId, "PLA care", newCare, "tophattwaffle");
        var uses = (await Blocks(shop).UsesAsync(blockId.Value)).ToDictionary(u => u.ListingId);
        Assert.Equal(TextBlockUseStatus.WillChange, uses[ids[1]].Status);
        Assert.Equal(TextBlockUseStatus.EditedByHand, uses[ids[2]].Status);
        Assert.Equal(TextBlockUseStatus.ChangedOnEtsy, uses[ids[3]].Status);
        Assert.Equal("A lovely vase.\n\n" + newCare + "\n\nThanks for looking!", uses[ids[1]].After);
        Assert.Contains(LineDiff.Compare(uses[ids[1]].Before, uses[ids[1]].After), l => l.Kind == LineDiff.Kind.Added && l.Text.EndsWith("direct sun."));

        Assert.Equal(1, await Blocks(shop).ApplyAsync(blockId.Value, "tophattwaffle"));
        Assert.Empty(shop.Writes); // applying only writes drafts
        Assert.Equal("Care: keep it cool.", await DescriptionAsync(shop, ids[2])); // the hand edit is left alone
        Assert.Equal(TextBlockUseStatus.UpToDate, (await Blocks(shop).UsesAsync(blockId.Value)).Single(u => u.ListingId == ids[1]).Status);

        // The review is the editor's own: listing 2 (hand edit, pending) can go, listing 3 is blocked by the change on Etsy.
        var review = (await Blocks(shop).ReviewAsync(blockId.Value)).ToDictionary(r => r.ListingId);
        Assert.True(review[ids[1]].Preview.CanPush);
        Assert.Equal(["description"], review[ids[1]].Preview.Changes.Select(c => c.Key));
        Assert.False(review.ContainsKey(ids[3]) && review[ids[3]].Preview.CanPush);

        var results = await Blocks(shop).PushAsync(blockId.Value, "tophattwaffle");
        var pushId = results.Single(r => r.ListingId == ids[1]).PushId!.Value;
        await RunPushAsync(shop, pushId);
        var patch = shop.Writes.Single(w => w.Path == "/v3/application/shops/42/listings/1");
        Assert.Contains(Uri.EscapeDataString("direct sun."), patch.Body.Replace("+", "%20"));

        var after = (await Blocks(shop).UsesAsync(blockId.Value)).Single(u => u.ListingId == ids[1]);
        Assert.Equal((TextBlockUseStatus.UpToDate, false), (after.Status, after.DescriptionPending)); // Etsy's copy reads back as the block
    }

    [Fact]
    public async Task Inserting_a_block_appends_it_once_and_links_it_and_deleting_keeps_the_text()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;
        var (blockId, _) = await Blocks(shop).SaveAsync(null, "PLA care", Care, "tophattwaffle");

        await Blocks(shop).InsertAsync(ids[4], blockId!.Value, "tophattwaffle");
        await Blocks(shop).InsertAsync(ids[4], blockId.Value, "tophattwaffle"); // already there: not added twice
        Assert.Equal("Description of Plain vase\nSecond line\n\n" + Care, await DescriptionAsync(shop, ids[4]));
        Assert.Equal(TextBlockUseStatus.UpToDate, Assert.Single(await Blocks(shop).ForListingAsync(ids[4])).Status);

        await Blocks(shop).DeleteAsync(blockId.Value, "tophattwaffle");
        Assert.Empty(await Blocks(shop).ForListingAsync(ids[4]));
        Assert.EndsWith(Care, await DescriptionAsync(shop, ids[4]));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public void Line_diff_shows_changes_with_context_and_gaps()
    {
        var diff = LineDiff.Compare("a\nb\nc\nd\ne\nf", "a\nb\nc\nd\nE\nf");
        Assert.Equal(
            [(LineDiff.Kind.Gap, ""), (LineDiff.Kind.Same, "d"), (LineDiff.Kind.Removed, "e"), (LineDiff.Kind.Added, "E"), (LineDiff.Kind.Same, "f")],
            diff.Select(l => (l.Kind, l.Text)));
        Assert.Empty(LineDiff.Compare("same", "same"));
    }
}
