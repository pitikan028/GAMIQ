using GAMIQ.Api.Data.Mongo;
using GAMIQ.Api.Dtos;
using GAMIQ.Api.Helpers;
using GAMIQ.Api.Models.Mongo;
using Microsoft.AspNetCore.Mvc;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GAMIQ.Api.Controllers;

[ApiController]
[Route("api/v1/products")]
public class ProductsController : ControllerBase
{
    private readonly MongoContext _mongo;

    public ProductsController(MongoContext mongo)
    {
        _mongo = mongo;
    }

    // GET /api/v1/products?page=1&pageSize=20&genre=RPG
    // CP3: projection. Mongo only sends back the fields of the list (no Description, no
    // Attributes), so a page of 100 games is much smaller. Always 2 commands: count + find.
    [HttpGet]
    public async Task<ActionResult<PagedResult<ProductSummaryResponse>>> GetProducts(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? genre = null)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var filter = string.IsNullOrWhiteSpace(genre)
            ? Builders<Product>.Filter.Empty
            : Builders<Product>.Filter.AnyEq(p => p.Genres, genre);

        var totalCount = await _mongo.Products.CountDocumentsAsync(filter);
        var products = await _mongo.Products.Find(filter)
            .SortByDescending(p => p.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Limit(pageSize)
            .Project(p => new ProductSummaryResponse(p.Id, p.Name, p.Price, p.Stock, p.Genres, p.Platforms))
            .ToListAsync();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Ok(new PagedResult<ProductSummaryResponse>(products, page, pageSize, totalCount, totalPages));
    }

    // GET /api/v1/products/{id}
    [HttpGet("{id}")]
    public async Task<ActionResult<ProductResponse>> GetProduct(string id)
    {
        if (!ObjectId.TryParse(id, out _))
        {
            return BadRequest(new { message = $"'{id}' is not a valid product id." });
        }

        var product = await _mongo.Products.Find(p => p.Id == id).FirstOrDefaultAsync();
        if (product is null)
        {
            return NotFound(new { message = $"Product {id} not found." });
        }

        return Ok(ToResponse(product));
    }

    // POST /api/v1/products - attributes can be any JSON object
    [HttpPost]
    public async Task<ActionResult<ProductResponse>> CreateProduct(CreateProductRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Name is required." });
        }

        var product = new Product
        {
            Name = request.Name.Trim(),
            Description = request.Description ?? string.Empty,
            Price = request.Price,
            Stock = request.Stock,
            Genres = request.Genres ?? new List<string>(),
            Platforms = request.Platforms ?? new List<string>(),
            Tags = request.Tags ?? new List<string>(),
            Attributes = JsonValues.ToPlain(request.Attributes),
            CreatedAt = DateTime.UtcNow
        };

        await _mongo.Products.InsertOneAsync(product);
        return CreatedAtAction(nameof(GetProduct), new { id = product.Id }, ToResponse(product));
    }

    private static ProductResponse ToResponse(Product p) =>
        new(p.Id, p.Name, p.Description, p.Price, p.Stock, p.Genres, p.Platforms, p.Tags, p.Attributes, p.CreatedAt);
}
