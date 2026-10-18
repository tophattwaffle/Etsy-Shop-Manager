using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Web.Modules.Editor;
using EtsyShopManager.Web.Modules.Listings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class AttributePersonalizationTests
{
    [Fact]
    public void Questions_read_from_Etsy_are_plain_text()
    {
        // Etsy returns personalization text HTML-encoded; the listing page shows the parsed questions.
        var questions = ManagedPersonalizationQuestion.Parse("""
            [{"question_id":1,"question_type":"text_input","question_text":"Name &amp; date","instructions":"We&#39;ll engrave it",
              "required":true,"max_allowed_characters":48}]
            """);

        var q = Assert.Single(questions);
        Assert.Equal("Name & date", q.QuestionText);
        Assert.Equal("We'll engrave it", q.Instructions);
        Assert.True(q.Required);
        Assert.Equal(48, q.MaxAllowedCharacters);
    }

    /// <summary>
    /// Listing 1 in category 1001 with Primary color Black and Height 0.75 inches, and one required personalization
    /// question (instructions HTML-encoded, as Etsy returns them). The category offers Primary color (one value),
    /// Occasion (up to two) and Height (a typed number on a scale, no list of values).
    /// </summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync()
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase");
        listing["_properties"] = new JsonArray(
            new JsonObject { ["property_id"] = 200L, ["property_name"] = "Primary color", ["value_ids"] = new JsonArray(1L), ["values"] = new JsonArray("Black") },
            new JsonObject { ["property_id"] = 400L, ["property_name"] = "Height", ["scale_id"] = 5L, ["value_ids"] = new JsonArray(91076551199L), ["values"] = new JsonArray("0.75") });
        listing["is_personalizable"] = true;
        listing["personalization"] = new JsonObject
        {
            ["personalization_questions"] = new JsonArray(new JsonObject
            {
                ["question_id"] = 1384924455475L, ["question_type"] = "text_input", ["question_text"] = "Personalization",
                ["instructions"] = "For &quot;Your Pick&quot; styles,\r\nlist each tile", ["required"] = true,
                ["max_allowed_characters"] = 512, ["max_allowed_files"] = null, ["add_on_price"] = null,
            }),
        };
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        db.TaxonomyProperties.AddRange(
            Property(200, "Primary color", multi: false, max: 5, values: """[{"value_id":1,"name":"Black","scale_id":null},{"value_id":9,"name":"Red","scale_id":null}]"""),
            Property(300, "Occasion", multi: true, max: 2,
                values: """[{"value_id":10,"name":"Birthday","scale_id":null},{"value_id":11,"name":"Wedding","scale_id":null},{"value_id":12,"name":"Halloween","scale_id":null}]"""),
            Property(400, "Height", multi: false, max: null, values: "[]", scales: """[{"scale_id":5,"display_name":"Inches"},{"scale_id":7,"display_name":"Centimeters"}]"""));
        await db.SaveChangesAsync();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static TaxonomyProperty Property(long id, string name, bool multi, int? max, string values, string scales = "[]") => new()
    {
        TaxonomyId = 1001, PropertyId = id, Name = name, DisplayName = name, SupportsAttributes = true, IsMultivalued = multi,
        MaxValuesAllowed = max, PossibleValuesJson = values, ScalesJson = scales,
    };

    private static ListingEditor Editor(FakeEtsyShop s) => s.Provider.GetRequiredService<ListingEditor>();

    private static async Task<AttributeProperty> PropertyAsync(FakeEtsyShop s, long id)
    {
        await using var db = s.Db.CreateDbContext();
        return (await AttributeEditor.LoadAsync(db, 1001))!.Single(p => p.PropertyId == id);
    }

    private static async Task<Listing> ListingAsync(FakeEtsyShop s, int id)
    {
        await using var db = s.Db.CreateDbContext();
        return await db.Listings.Include(l => l.EtsyState).SingleAsync(l => l.Id == id);
    }

    private static async Task RunPushAsync(FakeEtsyShop s, int pushId) =>
        await s.Provider.GetRequiredService<PushListingJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PushListingJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId), JobJson.Options),
        }, CancellationToken.None);

    private static async Task<ListingEditModel> ModelAsync(FakeEtsyShop s, int id) => (await Editor(s).LoadAsync(id))!.Model;

    [Fact]
    public async Task Imported_attributes_and_personalization_are_canonical_and_plan_no_calls()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var model = await ModelAsync(shop, id);

        Assert.Equal("""[{"property_id":200,"scale_id":null,"value_ids":[1],"values":["Black"]},{"property_id":400,"scale_id":5,"value_ids":[91076551199],"values":["0.75"]}]""",
            model.Etsy[ListingFields.Attributes]!.ToJsonString());
        var q = Assert.Single(PersonalizationEditor.FromManaged(model.Etsy[ListingFields.Personalization]));
        Assert.Equal(("text_input", "Personalization", "For \"Your Pick\" styles,\nlist each tile", true, 512L), // decoded, no Etsy ids
            (q.QuestionType, q.QuestionText, q.Instructions, q.Required, q.MaxAllowedCharacters));

        Assert.Empty(model.PendingFields);
        Assert.Empty(AttributeEditor.Plan(model.Etsy[ListingFields.Attributes], model.Local(ListingFields.Attributes)));
        Assert.Null(PersonalizationEditor.Call(model.Etsy[ListingFields.Personalization], model.Local(ListingFields.Personalization), 42, 1));
        Assert.Empty((await Editor(shop).PreviewAsync(id)).Changes);
    }

    [Fact]
    public void Attribute_values_are_kept_in_id_order()
    {
        var a = ManagedAttribute.From(new PropertyValue { PropertyId = 300, ValueIds = [11, 10], Values = ["Wedding", "Birthday"] });
        Assert.Equal([10L, 11L], a.ValueIds);
        Assert.Equal(["Birthday", "Wedding"], a.Values);
    }

    [Fact]
    public async Task Attribute_push_sends_one_call_per_changed_attribute_and_reconciles()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var attributes = (await ModelAsync(shop, id)).Local(ListingFields.Attributes);
        attributes = AttributeEditor.Set(attributes, await PropertyAsync(shop, 200), null, [9]);          // Black -> Red
        attributes = AttributeEditor.Set(attributes, await PropertyAsync(shop, 300), null, [11, 10]);     // new: Birthday, Wedding
        attributes = AttributeEditor.Remove(attributes, 400);                                              // Height removed
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Attributes] = attributes }, "tophattwaffle");

        var preview = await Editor(shop).PreviewAsync(id);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Equal([ListingFields.Attributes], preview.Changes.Select(c => c.Key));
        Assert.Contains("PUT /v3/application/shops/42/listings/1/properties/200  value_ids=9, values=Red  (Primary color)", preview.Warnings);
        Assert.Contains("PUT /v3/application/shops/42/listings/1/properties/300  value_ids=10,11, values=Birthday,Wedding  (Occasion)", preview.Warnings);
        Assert.Contains("DELETE /v3/application/shops/42/listings/1/properties/400  (remove Height)", preview.Warnings);
        Assert.Empty(shop.Writes);

        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);

        Assert.Equal([
            ("PUT", "/v3/application/shops/42/listings/1/properties/200", "value_ids=9&values=Red"),
            ("PUT", "/v3/application/shops/42/listings/1/properties/300", "value_ids=10%2C11&values=Birthday%2CWedding"),
            ("DELETE", "/v3/application/shops/42/listings/1/properties/400", ""),
        ], shop.Writes); // no updateListing: no listing field changed
        var l = await ListingAsync(shop, id);
        Assert.False(l.HasDraft);
        Assert.False(l.HasDrift);
        Assert.Equal([200L, 300L], l.Attributes.Select(a => a.PropertyId));
        await using var db = shop.Db.CreateDbContext();
        Assert.Equal(ListingPushStatus.Succeeded, (await db.ListingPushes.SingleAsync()).Status);
    }

    [Fact]
    public async Task Attributes_Etsy_would_refuse_block_the_push()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var attributes = (await ModelAsync(shop, id)).Local(ListingFields.Attributes)!.DeepClone().AsArray();
        attributes.Add(new JsonObject { ["property_id"] = 300L, ["scale_id"] = null, ["value_ids"] = new JsonArray(10L, 11L, 12L), ["values"] = new JsonArray("Birthday", "Wedding", "Halloween") });
        attributes[0]!["value_ids"] = new JsonArray(77L); // not one of Primary color's values
        attributes[0]!["values"] = new JsonArray("Teal");
        attributes[1]!["values"] = new JsonArray("2"); // Height is typed on Etsy only
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Attributes] = attributes }, "tophattwaffle");

        var blockers = (await Editor(shop).PreviewAsync(id)).Blockers;
        Assert.Contains("Primary color: \"Teal\" isn't one of Etsy's values for it.", blockers);
        Assert.Contains("Occasion takes at most 2 values.", blockers);
        Assert.Contains(blockers, b => b.StartsWith("Height takes a typed value that only Etsy can set"));

        // A category whose properties the app hasn't read can't take attribute changes yet.
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { ["taxonomy_id"] = 999L }, "tophattwaffle");
        Assert.Contains((await Editor(shop).PreviewAsync(id)).Blockers, b => b.StartsWith("The category's attributes aren't in the app yet"));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task Personalization_push_replaces_the_questions_keeping_ids_of_those_that_stay()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var questions = PersonalizationEditor.FromManaged((await ModelAsync(shop, id)).Local(ListingFields.Personalization));
        questions[0] = questions[0] with { QuestionText = "Your tiles", MaxAllowedCharacters = 256 };
        questions.Add(new ManagedPersonalizationQuestion("dropdown", "Color", null, false, null, null, null, ["Red", "Blue"]));
        questions.Add(PersonalizationEditor.NewQuestion() with { QuestionText = "Gift note", AddOnPrice = 2.5m });
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Personalization] = PersonalizationEditor.ToManaged(questions) }, "tophattwaffle");

        var preview = await Editor(shop).PreviewAsync(id);
        Assert.True(preview.CanPush, string.Join("; ", preview.Blockers));
        Assert.Contains(preview.Warnings, w => w.Contains("POST /v3/application/shops/42/listings/1/personalization?supports_multiple_personalization_questions=true  (3 questions"));

        var (pushId, _) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        await RunPushAsync(shop, pushId!.Value);

        var write = Assert.Single(shop.Writes);
        Assert.Equal(("POST", "/v3/application/shops/42/listings/1/personalization?supports_multiple_personalization_questions=true"), (write.Method, write.Path));
        var sent = JsonNode.Parse(write.Body)!["personalization_questions"]!.AsArray();
        Assert.Equal(1384924455475L, (long)sent[0]!["question_id"]!); // the question that stayed keeps its id
        Assert.Equal("For \"Your Pick\" styles,\nlist each tile", (string?)sent[0]!["instructions"]); // plain text
        Assert.Null(sent[1]!["question_id"]); // new ones have none (null fields are left out)
        Assert.Equal(["Red", "Blue"], sent[1]!["options"]!.AsArray().Select(o => (string)o!["label"]!));
        Assert.Null(sent[1]!["instructions"]);
        Assert.Equal(2.5m, (decimal)sent[2]!["add_on_price"]!);

        var l = await ListingAsync(shop, id);
        Assert.False(l.HasDraft, l.DraftJson); // Etsy's copy (new ids, encoded text, money) reads back as the same questions
        Assert.False(l.HasDrift);
        Assert.True(l.IsPersonalizable);
    }

    [Fact]
    public async Task Restoring_a_snapshot_from_before_version_5_leaves_attributes_and_personalization_alone()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        // A snapshot taken by the previous version: personalization null (none then), attributes with their names.
        var old = JsonNode.Parse((await ListingAsync(shop, id)).EtsyState!.ManagedJson)!.AsObject();
        old["title"] = "Old title";
        old[ListingFields.Personalization] = null;
        old[ListingFields.Attributes] = new JsonArray(new JsonObject
        {
            ["property_id"] = 200L, ["property_name"] = "Primary color", ["scale_id"] = null, ["value_ids"] = new JsonArray(9L), ["values"] = new JsonArray("Red"),
        });
        await using (var db = shop.Db.CreateDbContext())
        {
            db.ListingSnapshots.Add(new ListingSnapshot { ListingId = id, Reason = "Before push", ManagedJson = old.ToJsonString() });
            await db.SaveChangesAsync();
        }
        int snapshotId;
        await using (var db = shop.Db.CreateDbContext()) snapshotId = (await db.ListingSnapshots.SingleAsync()).Id;

        await Editor(shop).RestoreSnapshotAsync(snapshotId, "tophattwaffle");

        Assert.Equal(["title"], (await ModelAsync(shop, id)).PendingFields); // not "delete all questions"
    }

    [Fact]
    public void Reordered_questions_keep_their_own_ids()
    {
        const string etsy = """
            [{"question_id":1,"question_type":"text_input","question_text":"Front Text","required":true,"max_allowed_characters":48},
             {"question_id":2,"question_type":"text_input","question_text":"Back Text","required":true,"max_allowed_characters":48},
             {"question_id":3,"question_type":"dropdown","question_text":"Color","required":false,"options":[{"option_id":31,"label":"Red"},{"option_id":32,"label":"Blue"}]}]
            """;
        var local = PersonalizationEditor.ToManaged([
            new("text_input", "Back Text", null, true, 48, null, null, null),
            new("text_input", "Front side", null, true, 48, null, null, null), // renamed: takes the unused id of its type
            new("dropdown", "Color", null, false, null, null, null, ["Blue", "Green"]),
        ]);

        var body = PersonalizationEditor.Body(local, etsy).PersonalizationQuestions;

        Assert.Equal([2L, 1L, 3L], body.Select(q => q.QuestionId ?? 0));
        Assert.Equal([32L, null], body[2].Options!.Select(o => o.OptionId)); // Blue keeps its id, Green is new
    }

    [Fact]
    public async Task Removing_every_question_deletes_the_personalization()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        await Editor(shop).SaveAsync(id, new Dictionary<string, JsonNode?> { [ListingFields.Personalization] = new JsonArray() }, "tophattwaffle");
        var (pushId, blockers) = await Editor(shop).RequestPushAsync(id, "tophattwaffle");
        Assert.Empty(blockers);
        await RunPushAsync(shop, pushId!.Value);

        Assert.Equal(("DELETE", "/v3/application/shops/42/listings/1/personalization"), (Assert.Single(shop.Writes).Method, shop.Writes[0].Path));
        var l = await ListingAsync(shop, id);
        Assert.False(l.HasDraft);
        Assert.False(l.IsPersonalizable);
    }

    [Fact]
    public void Personalization_rules_from_Etsys_api_are_checked()
    {
        var questions = PersonalizationEditor.ToManaged([
            new("dropdown", "Pick a color for the front and the back please", "Not allowed here", false, null, null, null, ["Red", "Red"]),
            new("labeled_upload", "Photos", null, true, null, 2, null, ["Front"]),
            new("text_input", "Name", null, true, 0, null, null, null),
            new("text_input", "A", null, false, 10, null, null, null),
            new("text_input", "B", null, false, 10, null, null, null),
            new("text_input", "C", null, false, 10, null, null, null),
        ]);
        // Normalizing drops what doesn't apply: a drop-down's instructions.
        Assert.Null(PersonalizationEditor.FromManaged(questions)[0].Instructions);

        var problems = PersonalizationEditor.Problems(questions).ToList();
        Assert.Contains("6 personalization questions; Etsy allows 5.", problems);
        Assert.Contains("Personalization question 1's title is 46 characters; Etsy allows 45.", problems);
        Assert.Contains("Personalization question 1 has the same choice twice.", problems);
        Assert.Contains("Personalization question 2 needs one label per file (2).", problems);
        Assert.Contains("Personalization question 3 needs a character limit.", problems);
    }

    [Fact]
    public async Task Personalization_and_attributes_changed_on_Etsy_are_drift()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var etsy = shop.ListingJson(1);
        etsy["personalization"]!["personalization_questions"]![0]!["question_text"] = "Edited on etsy.com";
        etsy["_properties"]!.AsArray().RemoveAt(1);
        etsy["last_modified_timestamp"] = 1_760_000_500;
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);

        var l = await ListingAsync(shop, id);
        Assert.True(l.HasDrift);
        var drift = Assert.Single(shop.Events.Events.OfType<DriftDetected>());
        Assert.Equal([ListingFields.Attributes, ListingFields.Personalization], drift.Fields);
    }

    [Fact]
    public async Task A_clone_copies_the_personalization_questions_as_new_ones()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var store = shop.Provider.GetRequiredService<ImageStore>();
        await using (var db = shop.Db.CreateDbContext())
        {
            foreach (var image in await db.ListingImages.ToListAsync())
                image.FileName = await store.SaveAsync([1, 2, (byte)image.Rank], ".jpg");
            await db.SaveChangesAsync();
        }
        var cloner = shop.Provider.GetRequiredService<ListingCloner>();
        var choice = (await cloner.DefaultsAsync(id, FakeEtsyShop.ShopId)) with { Title = "Copy" };
        var plan = await cloner.PlanAsync(id, choice);
        Assert.Contains(plan.Calls, c => c.Contains("/personalization?supports_multiple_personalization_questions=true  (1 personalization question)"));
        Assert.DoesNotContain(plan.Warnings, w => w.Contains("Personalization"));

        var (cloneId, _) = await cloner.RequestAsync(id, choice, "tophattwaffle");
        await shop.Provider.GetRequiredService<ListingCloneJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = ListingCloneJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new ClonePayload(cloneId!.Value), JobJson.Options),
        }, CancellationToken.None);

        var draftId = (long)shop.Listings.Single(l => (string?)l["state"] == "draft")["listing_id"]!;
        var write = shop.Writes.Single(w => w.Path.Contains("/personalization"));
        Assert.Equal($"/v3/application/shops/42/listings/{draftId}/personalization?supports_multiple_personalization_questions=true", write.Path);
        var sent = Assert.Single(JsonNode.Parse(write.Body)!["personalization_questions"]!.AsArray());
        Assert.Null(sent!["question_id"]); // the source's ids belong to the source
        Assert.Equal("Personalization", (string?)sent["question_text"]);
        await using var check = shop.Db.CreateDbContext();
        Assert.True((await check.Listings.SingleAsync(l => l.EtsyListingId == draftId)).IsPersonalizable);
    }
}
