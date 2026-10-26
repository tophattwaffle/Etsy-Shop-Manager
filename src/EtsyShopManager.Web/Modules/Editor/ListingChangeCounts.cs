using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// How many listings have changes that aren't on Etsy yet, and how many were changed on Etsy, for the title-bar buttons
/// to the Pending changes and Changed on Etsy pages. Kept once for the whole app and counted again shortly after the
/// editor saves, discards or resolves edits (<see cref="Invalidate"/>) and after any job ends (pushes, imports and
/// template applies change them too), so every open page shows them live without each page counting.
/// </summary>
public sealed class ListingChangeCounts : IDisposable
{
    /// <summary>A bulk edit saves listing after listing; they are counted once, this long after the first.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<ListingChangeCounts> _logger;
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private bool _recountQueued;
    private Task? _loaded;
    private (int Pending, int ChangedOnEtsy) _counts;

    public ListingChangeCounts(IDbContextFactory<AppDbContext> dbFactory, IJobEvents events, TimeProvider time,
        ILogger<ListingChangeCounts> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
        _subscription = events.Subscribe(c => c.IsFinal, _ => RefreshAsync(), Debounce);
    }

    /// <summary>Raised after a count changed (on a background thread: components use InvokeAsync).</summary>
    public event Action? Changed;

    /// <summary>Listings the Pending changes page lists.</summary>
    public int Pending { get { lock (_gate) return _counts.Pending; } }

    /// <summary>Listings the Changed on Etsy page lists.</summary>
    public int ChangedOnEtsy { get { lock (_gate) return _counts.ChangedOnEtsy; } }

    /// <summary>Counts once, the first time a page asks.</summary>
    public Task EnsureLoadedAsync()
    {
        lock (_gate) return _loaded ??= RefreshAsync();
    }

    /// <summary>Listings' local edits changed: count again soon.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            if (_recountQueued) return; // the recount already coming will see this change too
            _recountQueued = true;
        }
        _ = RecountSoonAsync();
    }

    private async Task RecountSoonAsync()
    {
        try { await Task.Delay(Debounce, _time); }
        finally { lock (_gate) _recountQueued = false; }
        await RefreshAsync();
    }

    /// <summary>
    /// Listings whose local version differs from Etsy (pending), and those with a field Etsy changed since it was last
    /// agreed (changed on Etsy). The HasDraft and HasDrift flags only narrow the search; a flagged listing whose edits
    /// Etsy already holds has nothing to push.
    /// </summary>
    public static async Task<(int Pending, int ChangedOnEtsy)> CountAsync(AppDbContext db, CancellationToken ct = default)
    {
        var states = await db.Listings.AsNoTracking().Where(l => l.EtsyState != null && (l.HasDraft || l.HasDrift))
            .Select(l => new { l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson }).ToListAsync(ct);
        var models = states.Select(s => ListingEditModel.Parse(s.ManagedJson, s.BaseJson, s.DraftJson)).ToList();
        return (models.Count(m => m.PendingFields.Count > 0), models.Count(m => m.DriftFields.Count > 0));
    }

    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var counts = await CountAsync(db);
            bool changed;
            lock (_gate) (changed, _counts) = (_counts != counts, counts);
            if (changed) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count the listings with pending changes or changed on Etsy.");
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _refreshGate.Dispose();
    }
}
