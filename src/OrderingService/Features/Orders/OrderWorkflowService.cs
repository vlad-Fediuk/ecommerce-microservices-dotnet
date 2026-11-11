using System.Text.Json;
using ECommerce.Shared.Exceptions;
using Microsoft.EntityFrameworkCore;
using OrderingService.Data;
using OrderingService.Domain;
using OrderingService.Features.Orders.DTOs;

namespace OrderingService.Features.Orders;

public class OrderWorkflowService : IOrderingService
{
    private const int MaxIdempotencyKeyLength = 128;
    private const int MaxPageSize = 100;
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OrderingDbContext _dbContext;
    private readonly ICatalogLookup _catalog;

    public OrderWorkflowService(OrderingDbContext dbContext, ICatalogLookup catalog)
    {
        _dbContext = dbContext;
        _catalog = catalog;
    }

    public async Task<CreateOrderResult> CreateOrderAsync(Guid userId, string? idempotencyKey, CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        var scopedKey = BuildScopedKey(userId, idempotencyKey);

        if (scopedKey is not null)
        {
            var replayed = await TryReplayAsync(scopedKey, cancellationToken);
            if (replayed is not null)
            {
                return new CreateOrderResult(replayed, true);
            }
        }

        var aggregatedItems = request.Items
            .GroupBy(i => i.ProductId)
            .Select(g => new OrderItemRequest(g.Key, g.Sum(x => x.Quantity)))
            .ToList();

        var catalogItems = await _catalog.ReserveStockAsync(aggregatedItems, cancellationToken);

        var order = new Order
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            OrderNumber = $"ORD-{DateTime.UtcNow:yyyyMMdd}-{Guid.NewGuid().ToString("N")[..8].ToUpper()}",
            Status = OrderStatus.Pending,
            CreatedAtUtc = DateTime.UtcNow,
            IdempotencyKey = scopedKey,
            Items = aggregatedItems.Select(item =>
            {
                var catalogItem = catalogItems[item.ProductId];
                return new OrderItem
                {
                    Id = Guid.NewGuid(),
                    ProductId = item.ProductId,
                    ProductName = catalogItem.Title,
                    UnitPrice = Convert.ToDecimal(catalogItem.Price),
                    Quantity = item.Quantity
                };
            }).ToList()
        };

        order.TotalAmount = order.Items.Sum(i => i.UnitPrice * i.Quantity);

        var response = MapToResponse(order);

        _dbContext.Orders.Add(order);

        if (scopedKey is not null)
        {
            _dbContext.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = scopedKey,
                OrderId = order.Id,
                ResponseStatusCode = StatusCodes.Status201Created,
                ResponseBody = JsonSerializer.Serialize(response, SnapshotJsonOptions),
                CreatedAtUtc = DateTime.UtcNow
            });
        }

        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException) when (scopedKey is not null)
        {
            await _catalog.ReleaseStockAsync(aggregatedItems, CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            var concurrentlyCreated = await TryReplayAsync(scopedKey, cancellationToken);
            if (concurrentlyCreated is null)
            {
                throw;
            }

            return new CreateOrderResult(concurrentlyCreated, true);
        }
        catch (Exception)
        {
            await _catalog.ReleaseStockAsync(aggregatedItems, CancellationToken.None);
            throw;
        }

        return new CreateOrderResult(response, false);
    }

    public async Task<IReadOnlyList<OrderResponse>> GetUserOrdersAsync(Guid userId, OrderStatus? status, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        ValidatePaging(page, pageSize);

        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.UserId == userId);

        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        return await ExecutePagedQueryAsync(query, page, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderResponse>> GetAllOrdersAsync(OrderStatus? status, Guid? userId, int? page, int? pageSize, CancellationToken cancellationToken = default)
    {
        ValidatePaging(page, pageSize);

        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .AsQueryable();

        if (userId is not null && userId != Guid.Empty)
        {
            query = query.Where(o => o.UserId == userId.Value);
        }

        if (status is not null)
        {
            query = query.Where(o => o.Status == status);
        }

        return await ExecutePagedQueryAsync(query, page, pageSize, cancellationToken);
    }

    public async Task<OrderResponse> GetOrderByIdAsync(Guid orderId, Guid userId, bool canViewAll, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.Id == orderId);

        if (!canViewAll)
        {
            query = query.Where(o => o.UserId == userId);
        }

        var order = await query.FirstOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            throw new NotFoundException($"Замовлення з Id '{orderId}' не знайдено.");
        }

        return MapToResponse(order);
    }

    public async Task<OrderResponse> CancelOrderAsync(Guid orderId, Guid userId, bool canManageStatus, CancellationToken cancellationToken = default)
    {
        var query = _dbContext.Orders
            .Include(o => o.Items)
            .Where(o => o.Id == orderId);

        if (!canManageStatus)
        {
            query = query.Where(o => o.UserId == userId);
        }

        var order = await query.FirstOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            throw new NotFoundException($"Замовлення з Id '{orderId}' не знайдено.");
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            return MapToResponse(order);
        }

        if (order.Status is OrderStatus.Shipped or OrderStatus.Delivered)
        {
            throw new ConflictException("Неможливо скасувати замовлення, яке вже відправлено або доставлено.");
        }

        order.Status = OrderStatus.Cancelled;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        var itemsToRelease = order.Items
            .Select(i => new OrderItemRequest(i.ProductId, i.Quantity))
            .ToList();
        await _catalog.ReleaseStockAsync(itemsToRelease, cancellationToken);

        return MapToResponse(order);
    }

    public async Task<OrderResponse> UpdateOrderStatusAsync(Guid orderId, OrderStatus newStatus, CancellationToken cancellationToken = default)
    {
        var order = await _dbContext.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

        if (order is null)
        {
            throw new NotFoundException($"Замовлення з Id '{orderId}' не знайдено.");
        }

        if (order.Status == OrderStatus.Cancelled && newStatus != OrderStatus.Cancelled)
        {
            throw new ConflictException("Неможливо змінити статус вже скасованого замовлення.");
        }

        if (order.Status == OrderStatus.Delivered && newStatus != OrderStatus.Delivered)
        {
            throw new ConflictException("Неможливо змінити статус вже доставленого замовлення.");
        }

        if (!IsValidTransition(order.Status, newStatus))
        {
            throw new ConflictException($"Неприпустимий перехід статусу замовлення з '{order.Status}' на '{newStatus}'.");
        }

        var shouldReleaseStock = newStatus == OrderStatus.Cancelled && order.Status != OrderStatus.Cancelled;

        order.Status = newStatus;
        order.UpdatedAtUtc = DateTime.UtcNow;
        await _dbContext.SaveChangesAsync(cancellationToken);

        if (shouldReleaseStock)
        {
            var itemsToRelease = order.Items
                .Select(i => new OrderItemRequest(i.ProductId, i.Quantity))
                .ToList();
            await _catalog.ReleaseStockAsync(itemsToRelease, cancellationToken);
        }

        return MapToResponse(order);
    }

    private static bool IsValidTransition(OrderStatus current, OrderStatus next)
    {
        if (current == next)
        {
            return true;
        }

        return current switch
        {
            OrderStatus.Pending => next is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Cancelled,
            OrderStatus.Paid => next is OrderStatus.Shipped or OrderStatus.Cancelled,
            OrderStatus.Shipped => next is OrderStatus.Delivered,
            OrderStatus.Delivered => false,
            OrderStatus.Cancelled => false,
            _ => false
        };
    }

    private static void ValidatePaging(int? page, int? pageSize)
    {
        if (page is < 1)
        {
            throw new BadRequestException("Номер сторінки має бути більше 0.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new BadRequestException($"Розмір сторінки має бути в діапазоні від 1 до {MaxPageSize}.");
        }
    }

    private static async Task<IReadOnlyList<OrderResponse>> ExecutePagedQueryAsync(
        IQueryable<Order> query,
        int? page,
        int? pageSize,
        CancellationToken cancellationToken)
    {
        var ordered = query.OrderByDescending(o => o.CreatedAtUtc);

        var paged = pageSize is null
            ? ordered
            : ordered.Skip(((page ?? 1) - 1) * pageSize.Value).Take(pageSize.Value);

        var orders = await paged.ToListAsync(cancellationToken);
        return orders.Select(MapToResponse).ToList();
    }

    private static string? BuildScopedKey(Guid userId, string? idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        if (idempotencyKey.Length > MaxIdempotencyKeyLength)
        {
            throw new BadRequestException($"Idempotency-Key не повинен перевищувати {MaxIdempotencyKeyLength} символів.");
        }

        return $"{userId:N}:{idempotencyKey}";
    }

    private async Task<OrderResponse?> TryReplayAsync(string scopedKey, CancellationToken cancellationToken)
    {
        var record = await _dbContext.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == scopedKey, cancellationToken);

        return record is null
            ? null
            : JsonSerializer.Deserialize<OrderResponse>(record.ResponseBody, SnapshotJsonOptions);
    }

    private static OrderResponse MapToResponse(Order order)
    {
        var items = order.Items.Select(i => new OrderItemResponse(
            i.ProductId,
            i.ProductName,
            i.Quantity,
            i.UnitPrice,
            i.UnitPrice * i.Quantity
        )).ToList();

        return new OrderResponse(
            order.Id,
            order.OrderNumber,
            order.UserId,
            order.Status,
            order.TotalAmount,
            order.CreatedAtUtc,
            items
        );
    }
}
