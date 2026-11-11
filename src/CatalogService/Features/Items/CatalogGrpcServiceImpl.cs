using CatalogService.Models;
using ECommerce.Shared.Protos;
using Grpc.Core;

namespace CatalogService.Features.Items;

public class CatalogGrpcServiceImpl : CatalogGrpcService.CatalogGrpcServiceBase
{
    private readonly ICatalogRepository _repository;

    public CatalogGrpcServiceImpl(ICatalogRepository repository)
    {
        _repository = repository;
    }

    public override async Task<CatalogItemResponse> GetItem(GetItemRequest request, ServerCallContext context)
    {
        var itemId = ParseId(request.Id);
        var item = await _repository.GetByIdAsync(itemId, context.CancellationToken);
        if (item is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Item '{itemId}' was not found."));
        }

        return ToResponse(item);
    }

    public override async Task<CatalogItemsResponse> GetItems(GetItemsRequest request, ServerCallContext context)
    {
        var itemIds = request.Ids.Select(ParseId).Distinct().ToList();
        var items = await _repository.FindAsync(i => itemIds.Contains(i.Id), context.CancellationToken);

        var response = new CatalogItemsResponse();
        response.Items.AddRange(items.Select(ToResponse));
        return response;
    }

    public override async Task<CheckStockResponse> CheckStock(CheckStockRequest request, ServerCallContext context)
    {
        var itemId = ParseId(request.ItemId);
        var item = await _repository.GetByIdAsync(itemId, context.CancellationToken);
        if (item is null)
        {
            throw new RpcException(new Status(StatusCode.NotFound, $"Item '{itemId}' was not found."));
        }

        return new CheckStockResponse
        {
            IsAvailable = item.IsActive && item.StockQuantity >= request.Quantity,
            AvailableQuantity = item.StockQuantity
        };
    }

    public override async Task<ReserveStockResponse> ReserveStock(ReserveStockRequest request, ServerCallContext context)
    {
        var aggregated = request.Items
            .GroupBy(i => ParseId(i.ItemId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var itemIds = aggregated.Keys.ToList();
        var items = await _repository.FindAsync(i => itemIds.Contains(i.Id), context.CancellationToken);
        var itemsById = items.ToDictionary(i => i.Id);

        foreach (var (itemId, quantity) in aggregated)
        {
            if (quantity <= 0)
            {
                throw new RpcException(new Status(StatusCode.InvalidArgument, "Quantity must be greater than zero."));
            }

            if (!itemsById.TryGetValue(itemId, out var item) || !item.IsActive)
            {
                throw new RpcException(new Status(StatusCode.NotFound, $"Товар з Id '{itemId}' не знайдено."));
            }

            if (item.StockQuantity < quantity)
            {
                return new ReserveStockResponse
                {
                    Success = false,
                    FailedItemId = item.Id.ToString(),
                    AvailableQuantity = item.StockQuantity,
                    ErrorMessage = $"Недостатньо товару '{item.Title}' на складі. Доступно: {item.StockQuantity}, запитано: {quantity}."
                };
            }
        }

        var response = new ReserveStockResponse { Success = true };

        foreach (var (itemId, quantity) in aggregated)
        {
            var item = itemsById[itemId];
            item.StockQuantity -= quantity;
            _repository.Update(item);
            response.Items.Add(ToResponse(item));
        }

        await _repository.SaveChangesAsync(context.CancellationToken);

        return response;
    }

    public override async Task<ReleaseStockResponse> ReleaseStock(ReleaseStockRequest request, ServerCallContext context)
    {
        var aggregated = request.Items
            .GroupBy(i => ParseId(i.ItemId))
            .ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

        var itemIds = aggregated.Keys.ToList();
        var items = await _repository.FindAsync(i => itemIds.Contains(i.Id), context.CancellationToken);

        foreach (var item in items)
        {
            if (aggregated.TryGetValue(item.Id, out var quantity) && quantity > 0)
            {
                item.StockQuantity += quantity;
                _repository.Update(item);
            }
        }

        await _repository.SaveChangesAsync(context.CancellationToken);

        return new ReleaseStockResponse { Success = true };
    }

    private static Guid ParseId(string value)
    {
        if (!Guid.TryParse(value, out var id))
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, $"'{value}' is not a valid item identifier."));
        }

        return id;
    }

    private static CatalogItemResponse ToResponse(Item item) => new()
    {
        Id = item.Id.ToString(),
        Title = item.Title,
        Sku = item.Sku,
        Price = decimal.ToDouble(item.Price),
        StockQuantity = item.StockQuantity,
        IsActive = item.IsActive,
        CategoryId = item.CategoryId.ToString()
    };
}
