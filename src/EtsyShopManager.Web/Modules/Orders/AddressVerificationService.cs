using EtsyShopManager.Core.Domain;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Data;
using EtsyShopManager.Web.Infrastructure;
using EtsyShopManager.Web.Infrastructure.Usps;
using Microsoft.EntityFrameworkCore;

namespace EtsyShopManager.Web.Modules.Orders;

public class AddressVerificationService : IAddressVerificationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ISettingsStore _settings;
    private readonly IUspsClient _uspsClient;
    private readonly IJobQueue _jobQueue;
    private readonly IAuditLog _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<AddressVerificationService> _logger;

    public AddressVerificationService(
        IDbContextFactory<AppDbContext> dbFactory,
        ISettingsStore settings,
        IUspsClient uspsClient,
        IJobQueue jobQueue,
        IAuditLog audit,
        TimeProvider time,
        ILogger<AddressVerificationService> logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _uspsClient = uspsClient;
        _jobQueue = jobQueue;
        _audit = audit;
        _time = time;
        _logger = logger;
    }

    public async Task VerifyOrderAsync(int orderId, CancellationToken ct = default)
    {
        var enabled = await _settings.GetAsync(SettingKeys.UspsEnabled, ct);
        if (enabled is not ("1" or "true"))
        {
            _logger.LogDebug("Skipping address verification for order {OrderId}: USPS verification is disabled.", orderId);
            return;
        }

        var consumerKey = await _settings.GetAsync(SettingKeys.UspsConsumerKey, ct);
        var consumerSecret = await _settings.GetAsync(SettingKeys.UspsConsumerSecret, ct);
        if (string.IsNullOrWhiteSpace(consumerKey) || string.IsNullOrWhiteSpace(consumerSecret))
        {
            _logger.LogWarning("Skipping address verification for order {OrderId}: USPS credentials are not configured.", orderId);
            return;
        }

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
        {
            _logger.LogWarning("Order {OrderId} not found for address verification.", orderId);
            return;
        }

        var parsed = UsAddressParser.Parse(order);
        var addressHash = UsAddressParser.ComputeAddressHash(parsed);
        var now = _time.GetUtcNow().UtcDateTime;

        if (!parsed.IsUs)
        {
            _logger.LogDebug("Order {OrderId} is not a US address (country: {Country}, state: {State}); skipping.",
                orderId, parsed.CountryIso, parsed.State);
            order.AddressCheckStatus = AddressCheckStatuses.SkippedNonUs;
            order.AddressCheckAlert = null;
            order.AddressCheckHash = addressHash;
            order.AddressCheckedAtUtc = now;
            order.AddressCheckSuggested = null;
            order.AddressCheckRawReply = null;
            await db.SaveChangesAsync(ct);
            return;
        }

        var request = new UspsAddressRequest(
            StreetAddress: parsed.Street1,
            SecondaryAddress: string.IsNullOrWhiteSpace(parsed.Street2) ? null : parsed.Street2,
            City: string.IsNullOrWhiteSpace(parsed.City) ? null : parsed.City,
            State: string.IsNullOrWhiteSpace(parsed.State) ? null : parsed.State,
            ZipCode: string.IsNullOrWhiteSpace(parsed.Zip) ? null : parsed.Zip);

        var checkResult = await _uspsClient.StandardizeAddressAsync(request, consumerKey, consumerSecret, ct);
        var classification = AddressClassifier.Classify(parsed, checkResult);

        order.AddressCheckStatus = classification.Status;
        order.AddressCheckAlert = classification.Alert;
        order.AddressCheckHash = addressHash;
        order.AddressCheckedAtUtc = now;
        order.AddressCheckSuggested = classification.SuggestedAddress;
        order.AddressCheckRawReply = classification.RawReply;

        await db.SaveChangesAsync(ct);
        _logger.LogInformation("Verified address for order {OrderId}: status {Status}, alert: {Alert}",
            orderId, classification.Status, classification.Alert ?? "none");
    }

    public async Task<bool> ApplyUspsSuggestedAddressAsync(int orderId, string? user, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null || string.IsNullOrWhiteSpace(order.AddressCheckSuggested))
            return false;

        var suggested = order.AddressCheckSuggested;
        order.AddressOverride = suggested;
        await db.SaveChangesAsync(ct);

        await _audit.WriteAsync("order.address.use-usps", nameof(Order), order.EtsyReceiptId.ToString(),
            $"Applied USPS suggested address: {string.Join(", ", suggested.Split('\n'))}", user, ct);

        await QueueCheckAsync(order.Id, ct);
        return true;
    }

    public async Task<bool> DismissAlertAsync(int orderId, string? user, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == orderId, ct);
        if (order is null)
            return false;

        order.AddressCheckDismissedHash = order.AddressCheckHash;
        await db.SaveChangesAsync(ct);

        await _audit.WriteAsync("order.address.dismiss", nameof(Order), order.EtsyReceiptId.ToString(),
            "Dismissed address alert", user, ct);

        return true;
    }

    public Task QueueCheckAsync(int orderId, CancellationToken ct = default) =>
        _jobQueue.EnqueueAsync(new JobRequest
        {
            JobType = VerifyAddressJob.Type,
            Payload = new VerifyAddressPayload(orderId),
            DedupeKey = $"{VerifyAddressJob.Type}:{orderId}",
            Priority = 10,
            MaxAttempts = 5,
            CreatedBy = "system"
        }, ct);

    public async Task<int> QueueCheckForOpenOrdersAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var openOrders = await db.Orders.AsNoTracking()
            .Where(o => o.IsPaid && !o.IsShipped)
            .ToListAsync(ct);

        var queued = 0;
        foreach (var order in openOrders.Where(o => o.IsOpen))
        {
            var parsed = UsAddressParser.Parse(order);
            if (!parsed.IsUs) continue;

            await QueueCheckAsync(order.Id, ct);
            queued++;
        }

        _logger.LogInformation("Queued address checks for {Count} open US orders.", queued);
        return queued;
    }
}
