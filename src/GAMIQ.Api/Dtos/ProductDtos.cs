using System.ComponentModel.DataAnnotations;

namespace GAMIQ.Api.Dtos;

public record CreateProductRequest(
    [Required, StringLength(200)] string Name,
    string? Description,
    [Range(typeof(decimal), "0", "100000")] decimal Price,
    [Range(0, int.MaxValue)] int Stock,
    List<string>? Genres,
    List<string>? Platforms,
    List<string>? Tags,
    Dictionary<string, object>? Attributes);

public record ProductResponse(
    string Id,
    string Name,
    string Description,
    decimal Price,
    int Stock,
    List<string> Genres,
    List<string> Platforms,
    List<string> Tags,
    Dictionary<string, object> Attributes,
    DateTime CreatedAt);

// CP3: one line of GET /products. Only what a catalog list shows, the description and the
// attributes are left in Mongo (GET /products/{id} still returns everything).
public record ProductSummaryResponse(
    string Id,
    string Name,
    decimal Price,
    int Stock,
    List<string> Genres,
    List<string> Platforms);

public record PagedResult<T>(List<T> Items, int Page, int PageSize, long TotalCount, int TotalPages);
