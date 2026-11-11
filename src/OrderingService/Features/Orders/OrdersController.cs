using ECommerce.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderingService.Domain;
using OrderingService.Features.Orders.DTOs;

namespace OrderingService.Features.Orders;

[ApiController]
[Route("api/orders")]
[Route("api/v1/orders")]
[Tags("Orders")]
public class OrdersController : ControllerBase
{
    private const string IdempotencyKeyHeader = "Idempotency-Key";
    private const string IdempotencyReplayedHeader = "Idempotency-Replayed";

    private readonly IOrderingService _orderingService;

    public OrdersController(IOrderingService orderingService)
    {
        _orderingService = orderingService;
    }

    [HttpPost]
    [Authorize]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateOrder(
        [FromBody] CreateOrderRequest request,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var result = await _orderingService.CreateOrderAsync(userId, idempotencyKey, request, ct);

        if (result.Replayed)
        {
            Response.Headers[IdempotencyReplayedHeader] = "true";
        }

        return Created($"/api/orders/{result.Order.Id}", result.Order);
    }

    [HttpGet]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<OrderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetUserOrders(
        [FromQuery] OrderStatus? status,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        var userId = User.GetUserId();
        var orders = await _orderingService.GetUserOrdersAsync(userId, status, page, pageSize, ct);
        return Ok(orders);
    }

    [HttpGet("all")]
    [HasPermission(Permissions.Orders.ViewAll)]
    [ProducesResponseType(typeof(IReadOnlyList<OrderResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetAllOrders(
        [FromQuery] OrderStatus? status,
        [FromQuery] Guid? userId,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct)
    {
        var orders = await _orderingService.GetAllOrdersAsync(status, userId, page, pageSize, ct);
        return Ok(orders);
    }

    [HttpGet("{id:guid}")]
    [Authorize]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOrderById(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var canViewAll = User.HasPermission(Permissions.Orders.ViewAll);
        var order = await _orderingService.GetOrderByIdAsync(id, userId, canViewAll, ct);
        return Ok(order);
    }

    [HttpPost("{id:guid}/cancel")]
    [Authorize]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CancelOrder(Guid id, CancellationToken ct)
    {
        var userId = User.GetUserId();
        var canManageStatus = User.HasPermission(Permissions.Orders.ManageStatus);
        var order = await _orderingService.CancelOrderAsync(id, userId, canManageStatus, ct);
        return Ok(order);
    }

    [HttpPatch("{id:guid}/status")]
    [HasPermission(Permissions.Orders.ManageStatus)]
    [ProducesResponseType(typeof(OrderResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateOrderStatus(
        Guid id,
        [FromBody] UpdateOrderStatusRequest request,
        CancellationToken ct)
    {
        var updated = await _orderingService.UpdateOrderStatusAsync(id, request.Status, ct);
        return Ok(updated);
    }
}
