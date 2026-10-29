namespace EtsyShopManager.Web.Modules.Orders;

public interface IPersonalizationVerificationService
{
    /// <summary>Verifies personalization rules for the specified order.</summary>
    Task VerifyOrderAsync(int orderId, CancellationToken ct = default);

    /// <summary>Rechecks personalization rules for all currently open orders.</summary>
    Task<int> RecheckAllOpenOrdersAsync(CancellationToken ct = default);

    /// <summary>Dismisses the personalization alert for the current text hash.</summary>
    Task<bool> DismissAlertAsync(int orderId, string? user, CancellationToken ct = default);

    /// <summary>Queues a background job to recheck all open orders and notify connected circuits.</summary>
    Task QueueRecheckAllAsync(CancellationToken ct = default);
}
