using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Listings;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Editor;

/// <summary>
/// How many listings have changes that aren't on Etsy yet, for the title-bar Pending changes button. Kept once for the
/// whole app and counted again shortly after the editor saves, discards or resolves edits (<see cref="Invalidate"/>) and
/// after any job ends (pushes, imports and template applies change it too), so every open page shows it live without
/// each page counting.
/// </summary>
public sealed class PendingChangesCount : IDisposable
{
    /// <summary>A bulk edit saves listing after listing; they are counted once, this long after the first.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(300);

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<PendingChangesCount> _logger;
    private readonly IDisposable _subscription;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _gate = new();
    private bool _recountQueued;
    private Task? _loaded;
    private int _value;

    public PendingChangesCount(IDbContextFactory<AppDbContext> dbFactory, IJobEvents events, TimeProvider time,
        ILogger<PendingChangesCount> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
        _subscription = events.Subscribe(c => c.IsFinal, _ => RefreshAsync(), Debounce);
    }

    /// <summary>Raised after the count changed (on a background thread: components use InvokeAsync).</summary>
    public event Action? Changed;

    public int Value => Volatile.Read(ref _value);

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
    /// The listings the Pending changes page lists: those whose local version differs from Etsy. The HasDraft and
    /// HasDrift flags only narrow the search; a flagged listing whose edits Etsy already holds has nothing to push.
    /// </summary>
    public static async Task<int> CountAsync(AppDbContext db, CancellationToken ct = default)
    {
        var states = await db.Listings.AsNoTracking().Where(l => l.EtsyState != null && (l.HasDraft || l.HasDrift))
            .Select(l => new { l.EtsyState!.ManagedJson, l.EtsyState.BaseJson, l.DraftJson }).ToListAsync(ct);
        return states.Count(s => ListingEditModel.Parse(s.ManagedJson, s.BaseJson, s.DraftJson).PendingFields.Count > 0);
    }

    private async Task RefreshAsync()
    {
        await _refreshGate.WaitAsync();
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync();
            var count = await CountAsync(db);
            if (Interlocked.Exchange(ref _value, count) != count) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not count the listings with pending changes.");
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
