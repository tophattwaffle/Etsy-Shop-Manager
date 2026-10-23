using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class TextBlockTests
{
    private const string Care = "Care: PLA softens above 50 °C.\nKeep it out of hot cars.";
    private const string Pasted = "A lovely vase.\n\n" + Care + "\n\nThanks for looking!";

    /// <summary>Listings 1–3 with the care text pasted into their descriptions (Etsy's \r\n newlines), listing 4 without it.</summary>
    private static async Task<(FakeEtsyShop Shop, Dictionary<long, int> Ids)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        foreach (var id in new long[] { 1, 2, 3 })
        {
            var listing = FakeEtsyShop.Listing(id, $"Vase {id}");
            listing["description"] = Pasted.Replace("\n", "\r\n");
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

    private static async Task<int> CareBlockAsync(FakeEtsyShop s)
    {
        var (id, error) = await Blocks(s).SaveAsync(null, "PLA care", null, Care, "tophattwaffle");
        Assert.Null(error);
        return id!.Value;
    }

    private static async Task PushAsync(FakeEtsyShop s, int listingId)
    {
        s.Provider.GetRequiredService<RecordingJobQueue>().Requests.Clear();
        var (pushId, blockers) = await Editor(s).RequestPushAsync(listingId, "tophattwaffle");
        Assert.True(pushId is not null, string.Join("; ", blockers));
        await s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = pushId.Value, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId.Value), JobJson.Options),
        }, CancellationToken.None);
    }

    [Fact]
    public void Placeholders_render_tokenize_and_make_slugs()
    {
        Assert.Equal("pla-care-short", TextBlockMarkup.MakeSlug(" PLA care (short)! "));
        Assert.Equal("{{text.pla-care}}", TextBlockMarkup.Token("pla-care"));
        var block = new TextBlock { Slug = "pla-care", Text = Care };
        var (text, unknown) = TextBlockMarkup.Render("Hi\n{{ text.PLA-care }}\n{{text.gone}}", s => s == "pla-care" ? Care : null);
        Assert.Equal("Hi\n" + Care + "\n{{text.gone}}", text);
        Assert.Equal(["gone"], unknown);
        Assert.Equal("A lovely vase.\n\n{{text.pla-care}}\n\nThanks for looking!", TextBlockMarkup.Tokenize(Pasted, [block]));
        Assert.Equal(["pla-care"], TextBlockMarkup.Slugs("{{text.pla-care}} and {{text.pla-care}}"));
    }

    [Fact]
    public async Task A_placeholder_in_a_description_is_filled_in_on_Etsy_and_kept_in_the_app()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;
        await CareBlockAsync(shop);
        Assert.Equal("{{text.pla-care}}", TextBlockMarkup.Token((await Blocks(shop).AllAsync()).Single().Slug)); // slug made from the name

        const string source = "Plain vase, made to order.\n\n{{text.pla-care}}";
        Assert.Empty(await Blocks(shop).SaveDescriptionAsync(ids[4], source, "tophattwaffle"));
        Assert.Equal("Plain vase, made to order.\n\n" + Care, await DescriptionAsync(shop, ids[4])); // the draft has the text
        Assert.Equal(source, await Blocks(shop).SourceAsync(ids[4]));
        Assert.Empty(shop.Writes);

        await PushAsync(shop, ids[4]);
        var patch = Assert.Single(shop.Writes);
        Assert.DoesNotContain("%7B%7B", patch.Body); // Etsy never sees a placeholder
        Assert.Contains("Keep+it+out+of+hot+cars.", patch.Body);
        Assert.Equal(source, await Blocks(shop).SourceAsync(ids[4])); // still a placeholder after Etsy's copy is read back
        Assert.Equal(TextBlockUseStatus.UpToDate, Assert.Single(await Blocks(shop).UsesAsync(1)).Status);
    }

    [Fact]
    public async Task Editing_a_block_updates_every_description_using_it_through_the_reviewed_push()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;
        var blockId = await CareBlockAsync(shop);
        Assert.Equal([ids[1], ids[2], ids[3]], (await Blocks(shop).CandidatesAsync(blockId)).Select(c => c.ListingId).Order());
        Assert.Equal(2, await Blocks(shop).LinkAsync(blockId, [ids[1], ids[2], ids[4]], "tophattwaffle")); // 4 doesn't have the text
        Assert.Equal("A lovely vase.\n\n{{text.pla-care}}\n\nThanks for looking!", await Blocks(shop).SourceAsync(ids[1]));
        Assert.False((await shop.Db.CreateDbContext().Listings.SingleAsync(l => l.Id == ids[1])).HasDraft); // the text on Etsy is unchanged

        // Listing 2's description is changed on etsy.com.
        shop.ListingJson(2)["description"] = "Rewritten on Etsy";
        shop.ListingJson(2)["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        const string newCare = "Care: PLA softens above 50 °C.\nKeep it out of hot cars and direct sun.";
        await Blocks(shop).SaveAsync(blockId, "PLA care", "pla-care", newCare, "tophattwaffle");
        var uses = (await Blocks(shop).UsesAsync(blockId)).ToDictionary(u => u.ListingId);
        Assert.Equal(TextBlockUseStatus.WillChange, uses[ids[1]].Status);
        Assert.Equal(TextBlockUseStatus.ChangedOnEtsy, uses[ids[2]].Status);
        Assert.Equal("A lovely vase.\n\n" + newCare + "\n\nThanks for looking!", uses[ids[1]].After);
        Assert.Contains(LineDiff.Compare(uses[ids[1]].Before, uses[ids[1]].After), l => l.Kind == LineDiff.Kind.Added && l.Text.EndsWith("direct sun."));

        Assert.Equal(1, await Blocks(shop).ApplyAsync(blockId, "tophattwaffle"));
        Assert.Empty(shop.Writes); // applying only writes drafts
        var review = (await Blocks(shop).ReviewAsync(blockId)).ToDictionary(r => r.ListingId);
        Assert.True(review[ids[1]].Preview.CanPush);
        Assert.False(review[ids[2]].Preview.CanPush); // changed on Etsy: blocked until resolved
        await PushAsync(shop, ids[1]);
        Assert.Contains("direct+sun.", Assert.Single(shop.Writes).Body);
        Assert.Equal(TextBlockUseStatus.UpToDate, (await Blocks(shop).UsesAsync(blockId)).Single(u => u.ListingId == ids[1]).Status);

        // Taking Etsy's version for listing 2: the block's text is gone there, so it simply stops using the block.
        await Editor(shop).ResolveDriftAsync(ids[2], "description", DriftResolution.TakeEtsy, "tophattwaffle");
        Assert.Equal("Rewritten on Etsy", await Blocks(shop).SourceAsync(ids[2]));
        Assert.DoesNotContain(await Blocks(shop).UsesAsync(blockId), u => u.ListingId == ids[2]);
    }

    [Fact]
    public async Task Slug_renames_unknown_placeholders_stop_using_and_delete()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;
        var blockId = await CareBlockAsync(shop);
        await Blocks(shop).LinkAsync(blockId, [ids[1], ids[2]], "tophattwaffle");

        Assert.Null((await Blocks(shop).SaveAsync(blockId, "PLA care", "care", Care, "tophattwaffle")).Error);
        Assert.Equal("A lovely vase.\n\n{{text.care}}\n\nThanks for looking!", await Blocks(shop).SourceAsync(ids[1]));
        Assert.Equal("The placeholder name can only have lowercase letters, numbers and single hyphens (up to 60), like \"pla-care\".",
            (await Blocks(shop).SaveAsync(blockId, "PLA care", "Care Info", Care, "tophattwaffle")).Error);

        // A placeholder with no block stays as written and blocks the push.
        Assert.Equal(["missing"], await Blocks(shop).SaveDescriptionAsync(ids[4], "Hello {{text.missing}}", "tophattwaffle"));
        Assert.Contains((await Editor(shop).PreviewAsync(ids[4])).Blockers, b => b.StartsWith("The description has {{text.missing}}, which isn't a text template."));

        await Blocks(shop).DetachAsync(blockId, ids[2], "tophattwaffle");
        Assert.Equal(Pasted, await Blocks(shop).SourceAsync(ids[2])); // the text, now the listing's own
        await Blocks(shop).DeleteAsync(blockId, "tophattwaffle");
        Assert.Equal(Pasted, await Blocks(shop).SourceAsync(ids[1])); // listings keep the block's text
        Assert.Equal(Pasted, await DescriptionAsync(shop, ids[1]));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Links_from_the_first_version_become_placeholders()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;
        await using (var db = shop.Db.CreateDbContext())
        {
            // As the migration leaves them: a block with the "~id" slug and links that recorded the text they put in.
            var block = new TextBlock { Name = "PLA care", Slug = "~1", Text = Care };
            db.TextBlocks.Add(block);
            await db.SaveChangesAsync();
            db.ListingTextBlocks.AddRange(
                new ListingTextBlock { ListingId = ids[1], TextBlockId = block.Id, AppliedText = Care },
                new ListingTextBlock { ListingId = ids[2], TextBlockId = block.Id, AppliedText = "Old care text, edited by hand since" });
            await db.SaveChangesAsync();
        }

        await Blocks(shop).ConvertLinksAsync();

        Assert.Equal("pla-care", (await Blocks(shop).AllAsync()).Single().Slug);
        Assert.Equal("A lovely vase.\n\n{{text.pla-care}}\n\nThanks for looking!", await Blocks(shop).SourceAsync(ids[1]));
        // Listing 2's link recorded other text, but its description has the block's current text: it uses the block too.
        Assert.Equal([ids[1], ids[2]], (await Blocks(shop).UsesAsync(1)).Select(u => u.ListingId).Order());
        Assert.All(await Blocks(shop).UsesAsync(1), u => Assert.Equal(TextBlockUseStatus.UpToDate, u.Status));
        await using var check = shop.Db.CreateDbContext();
        Assert.Empty(check.ListingTextBlocks);
        Assert.False(await check.Listings.AnyAsync(l => l.HasDraft));
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

    [Fact]
    public async Task Builtin_placeholders_preserved_in_ListingDescription_even_without_text_templates()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        const string source = "Welcome to {{shop.name}}!\nItem: {{listing.title}}.\nCheck it out!";
        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4], source, "tophattwaffle");
        Assert.Empty(unknown);

        // Verify row is preserved in DB and not deleted by pruning bug
        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleOrDefaultAsync(d => d.ListingId == ids[4]);
            Assert.NotNull(row);
            Assert.Equal(source, row.Source);
            Assert.Equal("Welcome to TestShop!\nItem: Plain vase.\nCheck it out!", row.Rendered);
        }

        // Verify draft description has rendered text
        Assert.Equal("Welcome to TestShop!\nItem: Plain vase.\nCheck it out!", await DescriptionAsync(shop, ids[4]));
        Assert.Equal(source, await Blocks(shop).SourceAsync(ids[4]));

        // Push to Etsy
        await PushAsync(shop, ids[4]);
        var patch = Assert.Single(shop.Writes);
        Assert.DoesNotContain("%7B%7B", patch.Body); // Etsy never sees placeholders
        Assert.Contains("Welcome+to+TestShop%21", patch.Body);
        Assert.Contains("Item%3A+Plain+vase.", patch.Body);

        // After push: source is still preserved
        Assert.Equal(source, await Blocks(shop).SourceAsync(ids[4]));
    }

    [Fact]
    public async Task Editing_listing_title_dynamically_updates_rendered_description_in_draft_and_preserves_source()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        const string source = "Created by {{shop.name}}: {{listing.title}}";
        await Blocks(shop).SaveDescriptionAsync(ids[4], source, "tophattwaffle");

        // Now edit the title in ListingEditor
        await Editor(shop).SaveAsync(ids[4], new Dictionary<string, JsonNode?> { ["title"] = "Deluxe Plain Vase" }, "tophattwaffle");

        // The draft description should have automatically re-rendered with the new title
        Assert.Equal("Created by TestShop: Deluxe Plain Vase", await DescriptionAsync(shop, ids[4]));

        // The stored ListingDescription row must have re-rendered text while preserving the raw source
        await using (var db = shop.Db.CreateDbContext())
        {
            var row = await db.ListingDescriptions.SingleAsync(d => d.ListingId == ids[4]);
            Assert.Equal(source, row.Source);
            Assert.Equal("Created by TestShop: Deluxe Plain Vase", row.Rendered);
        }
    }

    [Fact]
    public async Task Circular_template_references_are_rejected_in_SaveAsync()
    {
        var (shop, _) = await SetupAsync();
        using var _ = shop;

        // Save first block
        var (idA, errA) = await Blocks(shop).SaveAsync(null, "Block A", "block-a", "Hello {{text.block-b}}", "tophattwaffle");
        Assert.Null(errA);
        Assert.NotNull(idA);

        // Try to save second block referencing first -> cycle!
        var (idB, errB) = await Blocks(shop).SaveAsync(null, "Block B", "block-b", "World {{text.block-a}}", "tophattwaffle");
        Assert.Null(idB);
        Assert.Equal("Template contains circular template references.", errB);
    }

    [Fact]
    public async Task Push_is_blocked_when_unresolved_placeholders_exist_in_description()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        // Save description with unresolved built-in placeholder
        var unknown = await Blocks(shop).SaveDescriptionAsync(ids[4], "Hello {{shop.invalid_field}} and {{listing.unknown_field}}", "tophattwaffle");
        Assert.Equal(["shop.invalid_field", "listing.unknown_field"], unknown);

        var preview = await Editor(shop).PreviewAsync(ids[4]);
        Assert.False(preview.CanPush);
        Assert.Contains(preview.Blockers, b => b.Contains("The description has unresolved placeholder(s): {{shop.invalid_field}}, {{listing.unknown_field}}. Fix the placeholder or verify the field exists."));

        var (pushId, blockers) = await Editor(shop).RequestPushAsync(ids[4], "tophattwaffle");
        Assert.Null(pushId);
        Assert.NotEmpty(blockers);
    }

    [Fact]
    public async Task Cloner_copies_ListingDescription_and_resolves_placeholders_for_target_shop_and_title()
    {
        var (shop, ids) = await SetupAsync();
        using var _ = shop;

        // Ensure images exist in image store so cloner plan can succeed
        var store = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var img in await db.ListingImages.Where(i => i.ListingId == ids[4]).ToListAsync())
                img.FileName = await store.SaveAsync([1, 2, (byte)img.Rank], ".jpg");
            await db.SaveChangesAsync();
        }

        const string source = "Welcome to {{shop.name}}!\nFeaturing: {{listing.title}}";
        await Blocks(shop).SaveDescriptionAsync(ids[4], source, "tophattwaffle");

        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = await cloner.DefaultsAsync(ids[4], FakeEtsyShop.ShopId);
        choice = choice with { Title = "Cloned Plain Vase" };

        var (cloneId, blockers) = await cloner.RequestAsync(ids[4], choice, "tophattwaffle");
        Assert.Empty(blockers);
        Assert.NotNull(cloneId);

        var cloneJob = shop.Provider.GetRequiredService<ListingCloneJob>();
        await cloneJob.HandleAsync(new JobExecutionContext
        {
            JobId = 1,
            JobType = ListingCloneJob.Type,
            Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId.Value), JobJson.Options),
        }, CancellationToken.None);

        // Check Etsy write body for createDraftListing
        var createWrite = shop.Writes.First(w => w.Method == "POST" && w.Path.Contains("/listings"));
        var query = System.Web.HttpUtility.ParseQueryString(createWrite.Body);
        Assert.Equal("Welcome to TestShop!\nFeaturing: Cloned Plain Vase", query["description"]);

        // Check cloned listing in DB
        await using var checkDb = shop.Db.CreateDbContext();
        var cloneRow = await checkDb.ListingClones.SingleAsync(c => c.Id == cloneId.Value);
        Assert.Equal(ListingCloneStatus.Succeeded, cloneRow.Status);
        Assert.NotNull(cloneRow.NewListingId);

        var clonedDesc = await checkDb.ListingDescriptions.SingleOrDefaultAsync(d => d.ListingId == cloneRow.NewListingId);
        Assert.NotNull(clonedDesc);
        Assert.Equal(source, clonedDesc.Source);
        Assert.Equal("Welcome to TestShop!\nFeaturing: Cloned Plain Vase", clonedDesc.Rendered);
    }
}
