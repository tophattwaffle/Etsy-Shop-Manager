using System.Text.Json;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Data;
using EtsyShopManager.Etsy;
using EtsyShopManager.Etsy.Models;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Listings;

/// <summary>
/// Caches Etsy's seller taxonomy (one call for the whole tree) and the properties of the taxonomy nodes that
/// listings actually use (one call each; fetching all ~3,000 nodes would waste the daily quota).
/// </summary>
public sealed class TaxonomyImporter(
    IDbContextFactory<AppDbContext> dbFactory,
    EtsyApiClient etsy,
    TimeProvider time,
    ILogger<TaxonomyImporter> logger)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    public async Task RefreshAsync(bool force, CancellationToken ct)
    {
        var auth = await AnyShopAuthAsync(ct);
        var now = time.GetUtcNow().UtcDateTime;

        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var newest = await db.TaxonomyNodes.MaxAsync(n => (DateTime?)n.ImportedAtUtc, ct);
            if (force || newest is null || newest < now - MaxAge)
            {
                var tree = await etsy.GetSellerTaxonomyNodesAsync(auth, ct);
                var nodes = Flatten(tree, now);
                // Replace in one transaction, so a failure can't leave the taxonomy empty.
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                await db.TaxonomyNodes.ExecuteDeleteAsync(ct);
                db.TaxonomyNodes.AddRange(nodes);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                logger.LogInformation("Imported {Count} taxonomy nodes", nodes.Count);
            }
        }

        await RefreshUsedPropertiesAsync(auth, force, now, ct);
    }

    private async Task RefreshUsedPropertiesAsync(EtsyAuth auth, bool force, DateTime now, CancellationToken ct)
    {
        List<long> due;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var used = await db.Listings.Where(l => l.TaxonomyId != null).Select(l => l.TaxonomyId!.Value).Distinct().ToListAsync(ct);
            var fresh = await db.TaxonomyProperties.GroupBy(p => p.TaxonomyId)
                .Where(g => g.Min(p => p.ImportedAtUtc) >= now - MaxAge)
                .Select(g => g.Key).ToListAsync(ct);
            due = force ? used : used.Except(fresh).ToList();
        }

        foreach (var taxonomyId in due)
        {
            var props = await etsy.GetTaxonomyPropertiesAsync(taxonomyId, auth, ct);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            await db.TaxonomyProperties.Where(p => p.TaxonomyId == taxonomyId).ExecuteDeleteAsync(ct);
            db.TaxonomyProperties.AddRange(props.GroupBy(p => p.PropertyId).Select(g => g.First()).Select(p => new TaxonomyProperty
            {
                TaxonomyId = taxonomyId,
                PropertyId = p.PropertyId,
                Name = p.Name,
                DisplayName = p.DisplayName,
                IsRequired = p.IsRequired,
                SupportsAttributes = p.SupportsAttributes,
                SupportsVariations = p.SupportsVariations,
                IsMultivalued = p.IsMultivalued,
                MaxValuesAllowed = p.MaxValuesAllowed,
                ScalesJson = JsonSerializer.Serialize(p.Scales ?? []),
                PossibleValuesJson = JsonSerializer.Serialize(p.PossibleValues ?? []),
                PossibleValueCount = p.PossibleValues?.Count ?? 0,
                ImportedAtUtc = now,
            }));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (due.Count > 0) logger.LogInformation("Imported properties for {Count} taxonomy node(s)", due.Count);
    }

    /// <summary>Taxonomy calls only need the API key, but the client authenticates as a shop; any healthy one will do.</summary>
    private async Task<EtsyAuth> AnyShopAuthAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var shopId = await db.Shops.Where(s => s.IsActive && s.Token != null && !s.Token.NeedsReauth)
            .OrderBy(s => s.Id).Select(s => (int?)s.Id).FirstOrDefaultAsync(ct);
        return shopId is { } id
            ? EtsyAuth.ForShop(id)
            : throw new JobPermanentFailureException("No connected shop to call Etsy with.");
    }

    public static List<TaxonomyNode> Flatten(IEnumerable<EtsyTaxonomyNode> roots, DateTime now)
    {
        var result = new List<TaxonomyNode>();
        var seen = new HashSet<long>();

        void Walk(EtsyTaxonomyNode node, long? parentId, string? parentPath)
        {
            if (!seen.Add(node.Id)) return;
            var path = parentPath is null ? node.Name : $"{parentPath} > {node.Name}";
            result.Add(new TaxonomyNode
            {
                Id = node.Id,
                ParentId = node.ParentId ?? parentId,
                Level = node.Level,
                Name = node.Name,
                FullPath = path,
                HasChildren = node.Children is { Count: > 0 },
                ImportedAtUtc = now,
            });
            foreach (var child in node.Children ?? []) Walk(child, node.Id, path);
        }

        foreach (var root in roots) Walk(root, null, null);
        return result;
    }
}
