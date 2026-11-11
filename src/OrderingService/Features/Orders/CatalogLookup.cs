using ECommerce.Shared.Exceptions;
using ECommerce.Shared.Protos;
using Grpc.Core;
using OrderingService.Features.Orders.DTOs;

namespace OrderingService.Features.Orders;

public class CatalogLookup : ICatalogLookup
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(1);

    private readonly CatalogGrpcService.CatalogGrpcServiceClient _client;
    private readonly ILogger<CatalogLookup> _logger;

    public CatalogLookup(CatalogGrpcService.CatalogGrpcServiceClient client, ILogger<CatalogLookup> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<Guid, CatalogItemResponse>> GetItemsAsync(IEnumerable<Guid> itemIds, CancellationToken cancellationToken)
    {
        var request = new GetItemsRequest();
        request.Ids.AddRange(itemIds.Select(id => id.ToString()));

        try
        {
            var response = await _client.GetItemsAsync(
                request,
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: cancellationToken);

            return response.Items.ToDictionary(i => Guid.Parse(i.Id));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            throw new NotFoundException("Товар не знайдено.");
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Unavailable)
        {
            _logger.LogWarning("Catalog gRPC call failed with {StatusCode}", ex.StatusCode);
            throw new ServiceUnavailableException("Каталог товарів тимчасово недоступний. Повторіть запит пізніше.");
        }
    }

    public async Task<IReadOnlyDictionary<Guid, CatalogItemResponse>> ReserveStockAsync(IReadOnlyList<OrderItemRequest> items, CancellationToken cancellationToken)
    {
        var request = new ReserveStockRequest();
        request.Items.AddRange(items.Select(i => new StockItemQuantity
        {
            ItemId = i.ProductId.ToString(),
            Quantity = i.Quantity
        }));

        try
        {
            var response = await _client.ReserveStockAsync(
                request,
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: cancellationToken);

            if (!response.Success)
            {
                throw new ConflictException(string.IsNullOrWhiteSpace(response.ErrorMessage)
                    ? "Недостатньо товару на складі."
                    : response.ErrorMessage);
            }

            return response.Items.ToDictionary(i => Guid.Parse(i.Id));
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.NotFound)
        {
            var message = !string.IsNullOrWhiteSpace(ex.Status.Detail) && ex.Status.Detail.StartsWith("Товар з Id")
                ? ex.Status.Detail
                : $"Товар з Id '{items.First().ProductId}' не знайдено.";
            throw new NotFoundException(message);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Unavailable)
        {
            _logger.LogWarning("Catalog gRPC ReserveStock failed with {StatusCode}", ex.StatusCode);
            throw new ServiceUnavailableException("Каталог товарів тимчасово недоступний. Повторіть запит пізніше.");
        }
    }

    public async Task ReleaseStockAsync(IReadOnlyList<OrderItemRequest> items, CancellationToken cancellationToken)
    {
        var request = new ReleaseStockRequest();
        request.Items.AddRange(items.Select(i => new StockItemQuantity
        {
            ItemId = i.ProductId.ToString(),
            Quantity = i.Quantity
        }));

        try
        {
            await _client.ReleaseStockAsync(
                request,
                deadline: DateTime.UtcNow.Add(CallTimeout),
                cancellationToken: cancellationToken);
        }
        catch (RpcException ex)
        {
            _logger.LogWarning("Catalog gRPC ReleaseStock failed with {StatusCode}: {Detail}", ex.StatusCode, ex.Status.Detail);
        }
    }

    public async Task<bool> IsReachableAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.CheckStockAsync(
                new CheckStockRequest { ItemId = Guid.Empty.ToString(), Quantity = 1 },
                deadline: DateTime.UtcNow.Add(ProbeTimeout),
                cancellationToken: cancellationToken);

            return true;
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.NotFound or StatusCode.InvalidArgument)
        {
            return true;
        }
        catch (RpcException)
        {
            return false;
        }
    }
}
