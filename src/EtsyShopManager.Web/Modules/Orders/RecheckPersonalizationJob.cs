using EtsyShopManager.Core.Events;
using EtsyShopManager.Core.Jobs;

namespace EtsyShopManager.Web.Modules.Orders;

public sealed class RecheckPersonalizationJob(IPersonalizationVerificationService service) : IJobHandler
{
    public const string Type = "orders.recheck_personalization";
    public string JobType => Type;
    public string DisplayName => "Verify personalization rules";

    public async Task HandleAsync(JobExecutionContext context, CancellationToken ct)
    {
        var count = await service.RecheckAllOpenOrdersAsync(ct);
        context.Result = $"Verified {count} open orders";
    }
}

public sealed class OrderReceivedPersonalizationVerificationHandler(
    IPersonalizationVerificationService service) : IDomainEventHandler<OrderReceived>
{
    public async Task HandleAsync(OrderReceived e, CancellationToken ct)
    {
        await service.VerifyOrderAsync(e.OrderId, ct);
    }
}
