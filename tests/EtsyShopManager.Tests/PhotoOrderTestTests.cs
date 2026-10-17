using System.Text.Json.Nodes;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class PhotoOrderTestTests
{
    /// <summary>One listing with three photos (ids 11, 12, 13 in that order).</summary>
    private static async Task<(FakeEtsyShop Shop, int ListingId)> SetupAsync(string state = "inactive")
    {
        var shop = new FakeEtsyShop();
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase", state: state);
        listing["images"] = new JsonArray(FakeEtsyShop.Image(1, 11, 1), FakeEtsyShop.Image(1, 12, 2), FakeEtsyShop.Image(1, 13, 3));
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        return (shop, (await db.Listings.SingleAsync()).Id);
    }

    private static PhotoOrderTester Tester(FakeEtsyShop s) => s.Provider.GetRequiredService<PhotoOrderTester>();

    private static Task RunAsync(FakeEtsyShop s, int testId) =>
        s.Provider.GetRequiredService<PhotoOrderTestJob>().HandleAsync(new JobExecutionContext
        {
            JobId = 1, JobType = PhotoOrderTestJob.Type, Attempt = 1,
            PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PhotoOrderTestPayload(testId), JobJson.Options),
        }, CancellationToken.None);

    private static async Task<PhotoOrderTest> TestAsync(FakeEtsyShop s)
    {
        await using var db = s.Db.CreateDbContext();
        return await db.PhotoOrderTests.SingleAsync();
    }

    private static string Reads(JsonNode? read) =>
        string.Join(" ", read!.AsArray().Select(p => $"{(long)p!["id"]!}:{(int)p["rank"]!}"));

    private static List<(long Id, int Rank)> Ranks(FakeEtsyShop s) =>
        [.. s.ListingJson(1)["images"]!.AsArray().Select(i => ((long)i!["listing_image_id"]!, (int)i["rank"]!)).OrderBy(x => x.Item2)];

    [Fact]
    public async Task A_run_sets_ranks_one_call_at_a_time_and_records_every_read_with_ranks()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, problem) = await Tester(shop).RequestAsync(id, [new(13, 1, false, true), new(11, 3, false, false)], "tophattwaffle");
        Assert.Null(problem);

        await RunAsync(shop, testId!.Value);

        Assert.Equal(["POST", "POST"], shop.Writes.Select(w => w.Method));
        Assert.Equal("listing_image_id=13&rank=1&overwrite=false&alt_text=Photo 3", shop.Writes[0].Body); // current alt text kept
        Assert.Equal("listing_image_id=11&rank=3&overwrite=false", shop.Writes[1].Body);

        var test = await TestAsync(shop);
        Assert.Equal(PhotoOrderTestStatus.Completed, test.Status);
        Assert.Equal("Completed 2 step(s); 2 left the photo at the rank asked for; every photo has its own rank at the end.", test.Outcome);
        var result = JsonNode.Parse(test.ResultJson!)!;
        Assert.Equal("11:1 12:2 13:3", Reads(result["before"]));
        Assert.Equal("11:1 13:1 12:2", Reads(result["steps"]![0]!["read_now"])); // Etsy doesn't renumber: a tie
        Assert.Equal("13:1 12:2 11:3", Reads(result["final_after_120s"]));
        Assert.Equal(true, (bool?)result["ranks_unique"]);
        Assert.NotNull(test.SnapshotId);
    }

    [Fact]
    public async Task Setting_every_rank_gives_each_photo_its_own_place()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, _) = await Tester(shop).RequestAsync(id, PhotoOrderTester.SetAllRanks([13, 11, 12]), "tophattwaffle");

        await RunAsync(shop, testId!.Value);

        Assert.Equal([(13L, 1), (11L, 2), (12L, 3)], Ranks(shop));
        Assert.Equal(true, (bool?)JsonNode.Parse((await TestAsync(shop)).ResultJson!)!["ranks_unique"]);
    }

    [Fact]
    public async Task The_fake_matches_the_ranks_Etsy_gave_in_TopHATTwaffles_three_runs()
    {
        // 2026-10-17, listing 1387884604, ranks from the import before the runs: 1, 2, 3, 4, 6, 7 (Etsy leaves gaps).
        var shop = new FakeEtsyShop();
        long[] ids = [3046382746, 3046382084, 3094110891, 4640710505, 4640710463, 4592467744];
        int[] ranks = [1, 2, 3, 4, 6, 7];
        var listing = FakeEtsyShop.Listing(1, "Galaxy vase", state: "draft");
        listing["images"] = new JsonArray([.. ids.Select((img, i) => (JsonNode)FakeEtsyShop.Image(1, img, ranks[i]))]);
        shop.Listings.Add(listing);
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        using var _ = shop;
        int id;
        await using (var db = shop.Db.CreateDbContext()) id = (await db.Listings.SingleAsync()).Id;

        async Task RunStepAsync(PhotoOrderStep step)
        {
            var (testId, problem) = await Tester(shop).RequestAsync(id, [step], "tophattwaffle");
            Assert.Null(problem);
            await RunAsync(shop, testId!.Value);
            shop.Provider.GetRequiredService<RecordingJobQueue>().Requests.Clear();
        }

        // Run 1, overwrite off: the photo gets rank 1 and nothing else moves, so two photos share rank 1.
        await RunStepAsync(new(4592467744, 1, false, true));
        Assert.Equal([3046382746L, 4592467744L], Ranks(shop).Where(x => x.Rank == 1).Select(x => x.Id).Order());
        Assert.Equal(6, Ranks(shop).Count);

        // Run 2, overwrite on: the photo Etsy showed at rank 1 then (4592467744; Etsy's tie order isn't fixed, so the
        // fake's stored order is set to match) drops off the listing.
        var stored = shop.ListingJson(1)["images"]!.AsArray().Select(i => i!.DeepClone()).OrderBy(i => (long)i["listing_image_id"]! == 4592467744 ? 0 : 1).ToArray();
        shop.ListingJson(1)["images"] = new JsonArray(stored);
        await RunStepAsync(new(4640710463, 1, true, true));
        Assert.DoesNotContain(Ranks(shop), x => x.Id == 4592467744);
        Assert.Equal(5, Ranks(shop).Count);
        Assert.Contains((4640710463L, 1), Ranks(shop));

        // Run 3 (a re-added photo, last): overwrite off to rank 1 ties with the photo already there.
        shop.ListingJson(1)["images"]!.AsArray().Add(FakeEtsyShop.Image(1, 8742141499, 8));
        await RunStepAsync(new(8742141499, 1, false, true));
        Assert.Contains((8742141499L, 1), Ranks(shop));
        Assert.True(Ranks(shop).Count(x => x.Rank == 1) >= 2);
    }
    [Fact]
    public async Task It_stops_at_the_first_rejection_and_records_what_Etsy_left()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, _) = await Tester(shop).RequestAsync(id, [new(13, 1, true, false), new(11, 3, false, false)], "tophattwaffle");
        shop.FailImageMovesWith = "The ListingImages for this Listing are in an inconsistent order.";

        await RunAsync(shop, testId!.Value);

        Assert.Single(shop.Writes); // the second step was never sent
        var test = await TestAsync(shop);
        Assert.Equal(PhotoOrderTestStatus.Stopped, test.Status);
        Assert.Contains("Stopped at step 1: Etsy answered 400", test.Outcome);
        var step = JsonNode.Parse(test.ResultJson!)!["steps"]![0]!;
        Assert.Contains("inconsistent order", (string?)step["answer"]);
        Assert.Equal("11:1 12:2 13:3", Reads(step["read_after_rejection"]));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("sold_out")]
    public async Task A_public_listing_is_refused_before_anything_is_written(string state)
    {
        var (shop, id) = await SetupAsync(state);
        using var _ = shop;

        var (testId, problem) = await Tester(shop).RequestAsync(id, [new(13, 1, false, false)], "tophattwaffle");

        Assert.Null(testId);
        Assert.Contains("Only a draft or inactive listing", problem);
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task A_listing_that_went_live_since_the_request_is_refused_by_the_run()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, _) = await Tester(shop).RequestAsync(id, [new(13, 1, false, false)], "tophattwaffle");
        shop.ListingJson(1)["state"] = "active"; // activated on etsy.com before the job ran

        await RunAsync(shop, testId!.Value);

        Assert.Empty(shop.Writes);
        var test = await TestAsync(shop);
        Assert.Equal(PhotoOrderTestStatus.Refused, test.Status);
        Assert.Contains("the listing is active on Etsy", test.Outcome);
    }

    [Fact]
    public async Task A_run_is_never_repeated_or_resumed()
    {
        var (shop, id) = await SetupAsync();
        using var _ = shop;
        var (testId, _) = await Tester(shop).RequestAsync(id, [new(13, 1, false, false)], "tophattwaffle");
        await RunAsync(shop, testId!.Value);
        shop.Writes.Clear();

        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunAsync(shop, testId.Value)); // "Retry" on the Jobs page
        Assert.Empty(shop.Writes);

        await using (var db = shop.Db.CreateDbContext())
            await db.PhotoOrderTests.ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, PhotoOrderTestStatus.Running));
        await Assert.ThrowsAsync<JobPermanentFailureException>(() => RunAsync(shop, testId.Value)); // after a restart
        Assert.Empty(shop.Writes);
        Assert.Contains("interrupted by a restart", (await TestAsync(shop)).Outcome);
    }

    [Fact]
    public void The_review_lists_exactly_what_will_be_sent()
    {
        var calls = PhotoOrderTester.Calls(42, 1, [new(13, 1, true, true)], new Dictionary<long, string?> { [13] = "Vase" });

        Assert.Contains("1. POST /v3/application/shops/42/listings/1/images  listing_image_id=13, rank=1, overwrite=true, alt_text=Vase" +
                        "  (stop here if Etsy rejects it)", calls);
        Assert.StartsWith("GET /v3/application/listings/1 (check it is still draft or inactive", calls[0]);
    }
}
