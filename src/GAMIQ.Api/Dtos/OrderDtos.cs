using System.ComponentModel.DataAnnotations;

namespace GAMIQ.Api.Dtos;

public record CreateOrderItemRequest(
    [Required] string ProductId,
    [Range(1, 100)] int Quantity);

public record CreateOrderRequest(
    [Range(1, int.MaxValue)] int UserId,
    [Required, MinLength(1)] List<CreateOrderItemRequest> Items);

public record OrderItemResponse(string ProductId, string ProductName, decimal UnitPrice, int Quantity);

public record OrderResponse(
    int OrderId,
    int UserId,
    decimal TotalAmount,
    string Status,
    DateTime CreatedAt,
    List<OrderItemResponse> Items);
