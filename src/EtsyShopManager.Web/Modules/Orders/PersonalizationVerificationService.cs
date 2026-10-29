using System.Security.Cryptography;
using System.Text;
using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

public class PersonalizationVerificationService : IPersonalizationVerificationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISettingsStore _settings;
    private readonly OrderColorService _colorService;
    private readonly IJobQueue _jobQueue;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<PersonalizationVerificationService> _logger;

    public PersonalizationVerificationService(
        IDbContextFactory<AppDbContext> dbFactory,
        ISettingsStore settings,
        OrderColorService colorService,
        IJobQueue jobQueue,
        IAuditLog audit,
        TimeProvider time,
        ILogger<PersonalizationVerificationService> logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _colorService = colorService;
        _jobQueue = jobQueue;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    public static string EffectivePersonalization(OrderItem item) =>
        item.PersonalizationOverride ?? OrderOverrides.BuyersPersonalization(item) ?? "";

    public static string ComputePersonalizationHash(Order order)
    {
        var canonical = string.Join("\n", order.Items
            .OrderBy(i => i.EtsyTransactionId)
            .Select(i => $"{i.EtsyTransactionId}:{EffectivePersonalization(i).Trim()}"));
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task<bool> IsEnabledAsync(CancellationToken ct)
    {
        var val = await _settings.GetAsync(SettingKeys.PersonalizationVerificationEnabled, ct);
        return val is not ("0" or "false");
    }

    public async Task VerifyOrderAsync(int orderId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found for personalization verification.", orderId);
            return;
        }

        var enabled = await IsEnabledAsync(ct);
        var hash = ComputePersonalizationHash(order);
        var now = _time.GetUtcNow().UtcDateTime;

        if (!enabled)
        {
            _logger.LogDebug("Personalization verification is disabled; skipping order {OrderId}.", orderId);
            order.PersonalizationCheckStatus = "Skipped";
            order.PersonalizationCheckAlert = null;
            order.PersonalizationCheckHash = hash;
            order.PersonalizationCheckedAtUtc = now;
            await db.SaveChangesAsync(ct);
            return;
        }

        var listingEtsyIds = order.Items.Select(i => i.EtsyListingId).OfType<long>().Distinct().ToList();
        var rulesByListing = await (from lr in db.ListingPersonalizationRules
                                    join l in db.Listings on lr.ListingId equals l.Id
                                    where listingEtsyIds.Contains(l.EtsyListingId)
                                    select new { l.EtsyListingId, lr.PersonalizationRule })
                                    .ToListAsync(ct);
        var rulesGrouped = rulesByListing
            .GroupBy(x => x.EtsyListingId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PersonalizationRule).ToList());

        var colorConfig = await _colorService.GetAsync(ct);
        var colorEngine = new OrderColorEngine(colorConfig);

        var failures = new List<(OrderItem Item, string Message)>();
        foreach (var item in order.Items)
        {
            if (item.EtsyListingId is not { } etsyId || !rulesGrouped.TryGetValue(etsyId, out var rules))
                continue;

            var text = EffectivePersonalization(item);
            foreach (var rule in rules)
            {
                var result = PersonalizationEngine.Evaluate(rule, text, item.Quantity, colorEngine);
                if (!result.IsValid)
                {
                    failures.Add((item, result.ErrorMessage ?? $"Failed rule '{rule.Name}'."));
                }
            }
        }

        order.PersonalizationCheckHash = hash;
        order.PersonalizationCheckedAtUtc = now;

        if (failures.Count > 0)
        {
            order.PersonalizationCheckStatus = "Failed";
            order.PersonalizationCheckAlert = string.Join("; ", failures.Select(f =>
                failures.Count > 1
                    ? $"{OrderPrintService.ShortTitle(f.Item.Title)}: {f.Message}"
                    : f.Message));
        }
        else
        {
            order.PersonalizationCheckStatus = "Ok";
            order.PersonalizationCheckAlert = null;
        }

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Verified personalization for order {OrderId}: status {Status}, alert: {Alert}",
            orderId, order.PersonalizationCheckStatus, order.PersonalizationCheckAlert ?? "none");
    }

    public async Task<int> RecheckAllOpenOrdersAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var openOrders = await db.Orders
            .Include(o => o.Items)
            .Where(o => o.IsPaid && !o.IsShipped)
            .ToListAsync(ct);
        var openList = openOrders.Where(o => o.IsOpen).ToList();

        var enabled = await IsEnabledAsync(ct);
        var now = _time.GetUtcNow().UtcDateTime;

        if (!enabled)
        {
            foreach (var o in openList)
            {
                o.PersonalizationCheckStatus = "Skipped";
                o.PersonalizationCheckAlert = null;
                o.PersonalizationCheckHash = ComputePersonalizationHash(o);
                o.PersonalizationCheckedAtUtc = now;
            }
            await db.SaveChangesAsync(ct);
            return openList.Count;
        }

        var allLinks = await (from lr in db.ListingPersonalizationRules
                              join l in db.Listings on lr.ListingId equals l.Id
                              select new { l.EtsyListingId, lr.PersonalizationRule })
                              .ToListAsync(ct);
        var rulesGrouped = allLinks
            .GroupBy(x => x.EtsyListingId)
            .ToDictionary(g => g.Key, g => g.Select(x => x.PersonalizationRule).ToList());

        var colorConfig = await _colorService.GetAsync(ct);
        var colorEngine = new OrderColorEngine(colorConfig);

        foreach (var order in openList)
        {
            var failures = new List<(OrderItem Item, string Message)>();
            foreach (var item in order.Items)
            {
                if (item.EtsyListingId is not { } etsyId || !rulesGrouped.TryGetValue(etsyId, out var rules))
                    continue;

                var text = EffectivePersonalization(item);
                foreach (var rule in rules)
                {
                    var result = PersonalizationEngine.Evaluate(rule, text, item.Quantity, colorEngine);
                    if (!result.IsValid)
                    {
                        failures.Add((item, result.ErrorMessage ?? $"Failed rule '{rule.Name}'."));
                    }
                }
            }

            order.PersonalizationCheckHash = ComputePersonalizationHash(order);
            order.PersonalizationCheckedAtUtc = now;

            if (failures.Count > 0)
            {
                order.PersonalizationCheckStatus = "Failed";
                order.PersonalizationCheckAlert = string.Join("; ", failures.Select(f =>
                    failures.Count > 1
                        ? $"{OrderPrintService.ShortTitle(f.Item.Title)}: {f.Message}"
                        : f.Message));
            }
            else
            {
                order.PersonalizationCheckStatus = "Ok";
                order.PersonalizationCheckAlert = null;
            }
        }

        await db.SaveChangesAsync(ct);
        return openList.Count;
    }

    public async Task<bool> DismissAlertAsync(int orderId, string? user, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null) return false;

        order.PersonalizationCheckDismissedHash = order.PersonalizationCheckHash;
        await db.SaveChangesAsync(ct);

        await _audit.WriteAsync("order.personalization.dismiss", nameof(Order), order.EtsyReceiptId.ToString(),
            "Dismissed personalization alert", user, ct);

        return true;
    }

    public Task QueueRecheckAllAsync(CancellationToken ct = default) =>
        _jobQueue.EnqueueAsync(new JobRequest
        {
            JobType = RecheckPersonalizationJob.Type,
            DedupeKey = RecheckPersonalizationJob.Type,
            RunAfterUtc = _time.GetUtcNow().UtcDateTime,
            Priority = 10,
        }, ct);
}
