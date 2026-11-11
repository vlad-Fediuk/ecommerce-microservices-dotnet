using System.Collections.Concurrent;
using ECommerce.Shared.Protos;
using Grpc.Core;

namespace Ordering.IntegrationTests.Infrastructure;

public class FakeCatalogClient : CatalogGrpcService.CatalogGrpcServiceClient
{
    private readonly ConcurrentDictionary<string, CatalogItemResponse> _items = new();
    private StatusCode? _failure;

    public CatalogItemResponse AddItem(string title, double price, int stockQuantity, bool isActive = true)
    {
        var item = new CatalogItemResponse
        {
            Id = Guid.NewGuid().ToString(),
            Title = title,
            Sku = $"SKU-{Guid.NewGuid():N}"[..16],
            Price = price,
            StockQuantity = stockQuantity,
            IsActive = isActive,
            CategoryId = Guid.NewGuid().ToString()
        };

        _items[item.Id] = item;
        return item;
    }

    public int GetStock(string itemId)
    {
        return _items.TryGetValue(itemId, out var item) ? item.StockQuantity : 0;
    }

    public IDisposable FailWith(StatusCode statusCode)
    {
        _failure = statusCode;
        return new FailureScope(this);
    }

    public override AsyncUnaryCall<CatalogItemResponse> GetItemAsync(GetItemRequest request, CallOptions options)
    {
        if (_failure is { } failure)
        {
            return Failed<CatalogItemResponse>(failure);
        }

        return _items.TryGetValue(request.Id, out var item)
            ? Succeeded(item)
            : Failed<CatalogItemResponse>(StatusCode.NotFound);
    }

    public override AsyncUnaryCall<CatalogItemsResponse> GetItemsAsync(GetItemsRequest request, CallOptions options)
    {
        if (_failure is { } failure)
        {
            return Failed<CatalogItemsResponse>(failure);
        }

        var response = new CatalogItemsResponse();
        response.Items.AddRange(request.Ids.Where(_items.ContainsKey).Select(id => _items[id].Clone()));
        return Succeeded(response);
    }

    public override AsyncUnaryCall<CheckStockResponse> CheckStockAsync(CheckStockRequest request, CallOptions options)
    {
        if (_failure is { } failure)
        {
            return Failed<CheckStockResponse>(failure);
        }

        return _items.TryGetValue(request.ItemId, out var item)
            ? Succeeded(new CheckStockResponse { IsAvailable = item.StockQuantity >= request.Quantity, AvailableQuantity = item.StockQuantity })
            : Failed<CheckStockResponse>(StatusCode.NotFound);
    }

    public override AsyncUnaryCall<ReserveStockResponse> ReserveStockAsync(ReserveStockRequest request, CallOptions options)
    {
        if (_failure is { } failure)
        {
            return Failed<ReserveStockResponse>(failure);
        }

        foreach (var reqItem in request.Items)
        {
            if (!_items.TryGetValue(reqItem.ItemId, out var item) || !item.IsActive)
            {
                return Failed<ReserveStockResponse>(StatusCode.NotFound, $"Товар з Id '{reqItem.ItemId}' не знайдено.");
            }

            if (item.StockQuantity < reqItem.Quantity)
            {
                return Succeeded(new ReserveStockResponse
                {
                    Success = false,
                    FailedItemId = item.Id,
                    AvailableQuantity = item.StockQuantity,
                    ErrorMessage = $"Недостатньо товару '{item.Title}' на складі. Доступно: {item.StockQuantity}, запитано: {reqItem.Quantity}."
                });
            }
        }

        var response = new ReserveStockResponse { Success = true };

        foreach (var reqItem in request.Items)
        {
            var item = _items[reqItem.ItemId];
            item.StockQuantity -= reqItem.Quantity;
            response.Items.Add(item.Clone());
        }

        return Succeeded(response);
    }

    public override AsyncUnaryCall<ReleaseStockResponse> ReleaseStockAsync(ReleaseStockRequest request, CallOptions options)
    {
        if (_failure is { } failure)
        {
            return Failed<ReleaseStockResponse>(failure);
        }

        foreach (var reqItem in request.Items)
        {
            if (_items.TryGetValue(reqItem.ItemId, out var item))
            {
                item.StockQuantity += reqItem.Quantity;
            }
        }

        return Succeeded(new ReleaseStockResponse { Success = true });
    }

    private static AsyncUnaryCall<T> Succeeded<T>(T response) => new(
        Task.FromResult(response),
        Task.FromResult(new Metadata()),
        () => Status.DefaultSuccess,
        () => new Metadata(),
        () => { });

    private static AsyncUnaryCall<T> Failed<T>(StatusCode statusCode, string detail = "Simulated catalog failure")
    {
        var status = new Status(statusCode, detail);
        return new AsyncUnaryCall<T>(
            Task.FromException<T>(new RpcException(status)),
            Task.FromResult(new Metadata()),
            () => status,
            () => new Metadata(),
            () => { });
    }

    private sealed class FailureScope : IDisposable
    {
        private readonly FakeCatalogClient _owner;

        public FailureScope(FakeCatalogClient owner)
        {
            _owner = owner;
        }

        public void Dispose() => _owner._failure = null;
    }
}
