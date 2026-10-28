namespace EtsyShopManager.Web.Modules.Orders;

public interface IAddressVerificationService
{
    /// <summary>
    /// Executes the USPS verification check for a single order and updates its verification fields.
    /// </summary>
    Task VerifyOrderAsync(int orderId, CancellationToken ct = default);

    /// <summary>
    /// Applies the USPS suggested address as the local AddressOverride, preserves recipient name,
    /// logs the audit action, and queues a recheck.
    /// </summary>
    Task<bool> ApplyUspsSuggestedAddressAsync(int orderId, string? user, CancellationToken ct = default);

    /// <summary>
    /// Dismisses the active address alert for the current address hash and logs the audit action.
    /// </summary>
    Task<bool> DismissAlertAsync(int orderId, string? user, CancellationToken ct = default);

    /// <summary>
    /// Enqueues a background address verification check job for the order.
    /// </summary>
    Task QueueCheckAsync(int orderId, CancellationToken ct = default);

    /// <summary>
    /// Enqueues background address verification checks for all open, unshipped US orders.
    /// </summary>
    Task<int> QueueCheckForOpenOrdersAsync(CancellationToken ct = default);
}
