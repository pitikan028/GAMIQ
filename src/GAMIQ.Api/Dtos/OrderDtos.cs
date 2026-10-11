using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

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
    // CP2: only sent once the API reads the new columns (ReadNew). Before that the field is left
    // out, so the answer looks exactly like it did in CP1.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CurrencyCode,
    string Status,
    DateTime CreatedAt,
    List<OrderItemResponse> Items);
