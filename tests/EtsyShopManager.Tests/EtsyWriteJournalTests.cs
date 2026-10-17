using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Http;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

public class EtsyWriteJournalTests
{
    private sealed class ListJournal : IEtsyWriteJournal
    {
        public List<EtsyWriteRecord> Records { get; } = [];

        public Task RecordAsync(EtsyWriteRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return Task.CompletedTask;
        }
    }

    private static (HttpClient Client, ListJournal Journal) Client(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var journal = new ListJournal();
        var handler = new EtsyWriteJournalHandler(journal, TimeProvider.System, NullLogger<EtsyWriteJournalHandler>.Instance)
        {
            InnerHandler = new ScriptedHandler((r, _) => respond(r)),
        };
        return (new HttpClient(handler) { BaseAddress = new Uri("https://api.etsy.example") }, journal);
    }

    [Fact]
    public async Task A_push_records_each_write_with_its_body_Etsys_reply_and_the_job()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        using var _ = shop;
        int id;
        await using (var db = shop.Db.CreateDbContext()) id = (await db.Listings.SingleAsync()).Id;
        var editor = shop.Provider.GetRequiredService<ListingEditor>();
        await editor.SaveAsync(id, new Dictionary<string, JsonNode?> { ["title"] = "New title" }, "tophattwaffle");
        var (pushId, _) = await editor.RequestPushAsync(id, "tophattwaffle");

        using (EtsyCallContext.Begin("job 7 etsy.listing.push"))
            await shop.Provider.GetRequiredService<PushListingJob>().HandleAsync(new EtsyShopManager.Core.Jobs.JobExecutionContext
            {
                JobId = 7, JobType = PushListingJob.Type, Attempt = 1,
                PayloadJson = System.Text.Json.JsonSerializer.Serialize(new PushPayload(pushId!.Value), EtsyShopManager.Core.Jobs.JobJson.Options),
            }, CancellationToken.None);

        await using var check = shop.Db.CreateDbContext();
        var write = Assert.Single(await check.EtsyWriteLogs.ToListAsync()); // the reads around the push are not writes
        Assert.Equal(("PATCH", "/v3/application/shops/42/listings/1", "title=New+title", 200),
            (write.Method, write.Path, write.RequestBody, write.StatusCode));
        Assert.Contains("\"listing_id\":1", write.ResponseBody);
        Assert.Equal("job 7 etsy.listing.push", write.Source);
    }

    [Fact]
    public async Task A_rejected_write_keeps_Etsys_answer_and_the_caller_still_reads_it()
    {
        var (client, journal) = Client(_ => ScriptedHandler.Json(HttpStatusCode.BadRequest,
            """{"error":"The ListingImages for this Listing are in an inconsistent order."}"""));

        var response = await client.PostAsync("/v3/application/shops/42/listings/1/images",
            new FormUrlEncodedContent([new("listing_image_id", "55"), new("rank", "3")]));

        Assert.Contains("inconsistent order", await response.Content.ReadAsStringAsync()); // still readable
        var record = Assert.Single(journal.Records);
        Assert.Equal((400, "listing_image_id=55&rank=3"), (record.StatusCode, record.RequestBody));
        Assert.Contains("inconsistent order", record.ResponseBody);
    }

    [Fact]
    public async Task Uploaded_files_are_summarised_not_stored()
    {
        var (client, journal) = Client(_ => ScriptedHandler.Json(HttpStatusCode.Created, """{"listing_image_id":9}"""));
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(new byte[1234]);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "image", "vase.jpg");
        form.Add(new StringContent("2"), "rank");

        await client.PostAsync("/v3/application/shops/42/listings/1/images", form);

        Assert.Equal("image=<file vase.jpg, 1234 bytes>&rank=2", Assert.Single(journal.Records).RequestBody);
    }

    [Fact]
    public async Task A_write_that_gets_no_answer_is_recorded_and_still_fails()
    {
        var (client, journal) = Client(_ => throw new HttpRequestException("connection reset"));

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.DeleteAsync("/v3/application/shops/42/listings/1/images/5"));

        var record = Assert.Single(journal.Records);
        Assert.Equal(("DELETE", (int?)null, "connection reset"), (record.Method, record.StatusCode, record.Error));
    }

    [Fact]
    public async Task Reads_and_OAuth_token_calls_are_never_recorded()
    {
        var (client, journal) = Client(_ => ScriptedHandler.Json(HttpStatusCode.OK, "{}"));

        await client.GetAsync("/v3/application/listings/1");
        await client.PostAsync("/v3/public/oauth/token", new FormUrlEncodedContent([new("refresh_token", "secret")]));

        Assert.Empty(journal.Records);
    }

    [Fact]
    public async Task A_journal_that_fails_never_breaks_the_write()
    {
        var handler = new EtsyWriteJournalHandler(new ThrowingJournal(), TimeProvider.System, NullLogger<EtsyWriteJournalHandler>.Instance)
        {
            InnerHandler = new ScriptedHandler((_, _) => ScriptedHandler.Json(HttpStatusCode.OK, "{}")),
        };
        var client = new HttpClient(handler) { BaseAddress = new Uri("https://api.etsy.example") };

        var response = await client.PutAsync("/v3/application/listings/1/inventory", new StringContent("{}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class ThrowingJournal : IEtsyWriteJournal
    {
        public Task RecordAsync(EtsyWriteRecord record, CancellationToken ct = default) => throw new InvalidOperationException("disk full");
    }
}

public class EtsyWriteRetentionTests
{
    [Fact]
    public async Task Cleanup_drops_journal_entries_past_their_retention()
    {
        using var testDb = new TestDb();
        var time = new ImmediateTimeProvider();
        var now = time.Now.UtcDateTime;
        await using (var db = testDb.CreateDbContext())
        {
            db.EtsyWriteLogs.AddRange(
                new EtsyShopManager.Core.Domain.EtsyWriteLog { AtUtc = now.AddDays(-91), Method = "PATCH", Path = "/old" },
                new EtsyShopManager.Core.Domain.EtsyWriteLog { AtUtc = now.AddDays(-89), Method = "PATCH", Path = "/recent" });
            await db.SaveChangesAsync();
        }
        var options = Microsoft.Extensions.Options.Options.Create(new EtsyShopManager.Web.Infrastructure.Jobs.JobOptions());

        await new EtsyShopManager.Web.Modules.JobsCleanupJob(testDb, options, time)
            .HandleAsync(new EtsyShopManager.Core.Jobs.JobExecutionContext { JobId = 1, JobType = "jobs.cleanup", Attempt = 1 }, CancellationToken.None);

        await using var check = testDb.CreateDbContext();
        Assert.Equal(["/recent"], await check.EtsyWriteLogs.Select(w => w.Path).ToListAsync());
    }
}
