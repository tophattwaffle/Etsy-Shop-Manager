using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;
using EtsyShopManager.Core.Services;
using EtsyShopManager.Web.Infrastructure;

namespace EtsyShopManager.Web.Modules.Orders;

public sealed record VerifyAddressPayload(int OrderId);

public sealed class VerifyAddressJob(IAddressVerificationService verificationService) : IJobHandler
{
    public const string Type = "orders.verify_address";
    public string JobType => Type;
    public string DisplayName => "Verify shipping address";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var payload = context.GetPayload<VerifyAddressPayload>();
        if (payload is null)
            throw new InvalidOperationException("Missing payload for verify address job.");

        await verificationService.VerifyOrderAsync(payload.OrderId, ct);
        context.Result = "Address verified";
    }
}

public sealed class OrderReceivedAddressVerificationHandler(
    ISettingsStore settings,
    IAddressVerificationService verificationService) : IDomainEventHandler<OrderReceived>
{
    public async Task HandleAsync(OrderReceived e, CancellationToken ct)
    {
        var enabled = await settings.GetAsync(Core.Domain.SettingKeys.UspsEnabled, ct);
        if (enabled is not ("1" or "true"))
            return;

        await verificationService.QueueCheckAsync(e.OrderId, ct);
    }
}
