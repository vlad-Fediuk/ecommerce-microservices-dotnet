using ECommerce.Shared.Protos;
using OrderingService.Features.Orders.DTOs;

namespace OrderingService.Features.Orders;

public interface ICatalogLookup
{
    Task<IReadOnlyDictionary<Guid, CatalogItemResponse>> GetItemsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<Guid, CatalogItemResponse>> ReserveStockAsync(IReadOnlyList<OrderItemRequest> items, CancellationToken cancellationToken);
    Task ReleaseStockAsync(IReadOnlyList<OrderItemRequest> items, CancellationToken cancellationToken);
    Task<bool> IsReachableAsync(CancellationToken cancellationToken);
}
