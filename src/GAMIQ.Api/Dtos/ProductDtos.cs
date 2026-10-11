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

public record PagedResult<T>(List<T> Items, int Page, int PageSize, long TotalCount, int TotalPages);
