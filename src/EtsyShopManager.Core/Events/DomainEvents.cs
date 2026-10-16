using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace EtsyShopManager.Core.Events;

/// <summary>
/// In-process domain event. Modules subscribe by registering <see cref="IDomainEventHandler{TEvent}"/>,
/// so new features hook in without editing existing code.
/// </summary>
public interface IDomainEvent
{
    DateTime OccurredAtUtc { get; }
}

public interface IDomainEventHandler<in TEvent> where TEvent : IDomainEvent
{
    Task HandleAsync(TEvent domainEvent, CancellationToken ct);
}

public interface IDomainEventPublisher
{
    Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default) where TEvent : IDomainEvent;
}

/// <summary>
/// Runs every handler for the event in its own DI scope. A failing handler is logged and does not
/// stop the others or the publisher.
/// </summary>
public sealed class DomainEventPublisher(IServiceScopeFactory scopeFactory, ILogger<DomainEventPublisher> logger)
    : IDomainEventPublisher
{
    public async Task PublishAsync<TEvent>(TEvent domainEvent, CancellationToken ct = default)
        where TEvent : IDomainEvent
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IDomainEventHandler<TEvent>>();
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(domainEvent, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Handler {Handler} failed for {Event}", handler.GetType().Name, typeof(TEvent).Name);
            }
        }
    }
}

// ---- Events. Later phases add ListingPushed, DriftDetected, StockRefilled… ----

public sealed record ShopConnected(int ShopId, long EtsyShopId, string ShopName, bool IsReconnect) : IDomainEvent
{
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record ShopDisconnected(int ShopId, string ShopName) : IDomainEvent
{
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

public sealed record ShopNeedsReauth(int ShopId, string Reason) : IDomainEvent
{
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>A paid order this app had not seen before (not raised for the initial backfill).</summary>
public sealed record OrderReceived(int ShopId, int OrderId, long EtsyReceiptId) : IDomainEvent
{
    public DateTime OccurredAtUtc { get; init; } = DateTime.UtcNow;
}
