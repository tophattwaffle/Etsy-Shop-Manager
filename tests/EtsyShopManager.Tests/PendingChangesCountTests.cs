using System.Text.Json.Nodes;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Web.Modules.Editor;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace EtsyShopManager.Tests;

/// <summary>The title-bar Pending changes count: kept live for every page without each page counting.</summary>
public class PendingChangesCountTests
{
    /// <summary>Hands the subscription to the test, so job changes are delivered when the test says.</summary>
    private sealed class ManualJobEvents : IJobEvents
    {
        public Func<JobChanged, bool>? Filter { get; private set; }
        public Func<IReadOnlyList<JobChanged>, Task>? Deliver { get; private set; }
        public void Publish(JobChanged change) { }

        public IDisposable Subscribe(Func<JobChanged, bool> filter, Func<IReadOnlyList<JobChanged>, Task> onChange, TimeSpan? debounce = null)
        {
            (Filter, Deliver) = (filter, onChange);
            return new Noop();
        }

        private sealed class Noop : IDisposable { public void Dispose() { } }
    }

    private static async Task<(FakeEtsyShop Shop, ManualJobEvents Events, PendingChangesCount Count, ListingEditor Editor, int Galaxy)> BuildAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Galaxy vase"));
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Moon vase"));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        await using var db = shop.Db.CreateDbContext();
        var galaxy = await db.Listings.Where(l => l.EtsyListingId == 1).Select(l => l.Id).SingleAsync();

        var events = new ManualJobEvents();
        var count = new PendingChangesCount(shop.Db, events, shop.Time, NullLogger<PendingChangesCount>.Instance);
        var editor = ActivatorUtilities.CreateInstance<ListingEditor>(shop.Provider, count);
        return (shop, events, count, editor, galaxy);
    }

    /// <summary>Waits for the count to be raised with the expected value (the recount runs in the background).</summary>
    private static async Task ChangesToAsync(PendingChangesCount count, int expected, Func<Task> act)
    {
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged() { if (count.Value == expected) changed.TrySetResult(); }
        count.Changed += OnChanged;
        try
        {
            await act();
            await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally { count.Changed -= OnChanged; }
        Assert.Equal(expected, count.Value);
    }

    [Fact]
    public async Task Saving_and_discarding_edits_in_the_editor_updates_the_count()
    {
        var (shop, _, count, editor, galaxy) = await BuildAsync();
        using var _s = shop;
        await count.EnsureLoadedAsync();
        Assert.Equal(0, count.Value);

        await ChangesToAsync(count, 1, () =>
            editor.SaveAsync(galaxy, new Dictionary<string, JsonNode?> { ["title"] = "Galaxy vase, large" }, "tophattwaffle"));
        await ChangesToAsync(count, 0, () => editor.DiscardAsync(galaxy, null, "tophattwaffle"));
        Assert.Empty(shop.Writes);
    }

    [Fact]
    public async Task A_finished_job_recounts_and_jobs_still_queued_or_running_do_not()
    {
        var (shop, events, count, _, galaxy) = await BuildAsync();
        using var _s = shop;
        await count.EnsureLoadedAsync();
        Assert.Equal(0, count.Value);

        // Something other than the editor (a template apply or bulk edit job) left the listing with a draft.
        var other = shop.Provider.GetRequiredService<ListingEditor>(); // no count wired in: only the job tells it
        await other.SaveAsync(galaxy, new Dictionary<string, JsonNode?> { ["title"] = "Galaxy vase, large" }, "tophattwaffle");

        JobChanged Change(JobStatus status) => new(1, "listing.push", "Push listing changes", FakeEtsyShop.ShopId, status, "tophattwaffle", null, null);
        Assert.False(events.Filter!(Change(JobStatus.Pending)));
        Assert.False(events.Filter!(Change(JobStatus.Running)));
        Assert.True(events.Filter!(Change(JobStatus.Succeeded)));
        await ChangesToAsync(count, 1, () => events.Deliver!([Change(JobStatus.Succeeded)]));
    }

    [Fact]
    public async Task Counts_what_the_Pending_changes_page_counts()
    {
        var (shop, _, count, editor, galaxy) = await BuildAsync();
        using var _s = shop;
        await editor.SaveAsync(galaxy, new Dictionary<string, JsonNode?> { ["title"] = "Galaxy vase, large" }, "tophattwaffle");
        await count.EnsureLoadedAsync();

        Assert.Equal(await shop.Provider.GetRequiredService<PendingChangesService>().CountAsync(), count.Value);
        Assert.Equal(1, count.Value);
    }
}
