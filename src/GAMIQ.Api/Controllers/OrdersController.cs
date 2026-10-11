using GAMIQ.Api.Data;
using GAMIQ.Api.Data.Mongo;
using GAMIQ.Api.Dtos;
using GAMIQ.Api.Models.Mongo;
using GAMIQ.Api.Models.Postgres;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using MongoDB.Bson;
using MongoDB.Driver;

namespace GAMIQ.Api.Controllers;

[ApiController]
[Route("api/v1/orders")]
public class OrdersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MongoContext _mongo;

    public OrdersController(AppDbContext db, MongoContext mongo)
    {
        _db = db;
        _mongo = mongo;
    }

    // POST /api/v1/orders
    // Postgres: insert order + items and take money from the wallet (one SQL transaction).
    // Mongo: decrease stock for each product. If a stock update fails we put back the stock we
    // already took and roll back the SQL transaction, so both sides stay in sync.
    [HttpPost]
    public async Task<ActionResult<OrderResponse>> CreateOrder(CreateOrderRequest request)
    {
        var badId = request.Items.FirstOrDefault(i => !ObjectId.TryParse(i.ProductId, out _));
        if (badId is not null)
        {
            return BadRequest(new { message = $"'{badId.ProductId}' is not a valid product id." });
        }

        // same product sent twice -> merge into one line
        var lines = request.Items
            .GroupBy(i => i.ProductId)
            .Select(g => (ProductId: g.Key, Quantity: g.Sum(i => i.Quantity)))
            .ToList();

        var user = await _db.Users.Include(u => u.Account).FirstOrDefaultAsync(u => u.UserId == request.UserId);
        if (user is null)
        {
            return NotFound(new { message = $"User {request.UserId} not found." });
        }

        if (user.Account is null)
        {
            return UnprocessableEntity(new { message = "User has no wallet account." });
        }

        var ids = lines.Select(l => l.ProductId).ToList();
        var products = await _mongo.Products.Find(Builders<Product>.Filter.In(p => p.Id, ids)).ToListAsync();
        var productById = products.ToDictionary(p => p.Id);

        var orderItems = new List<OrderItem>();
        foreach (var line in lines)
        {
            if (!productById.TryGetValue(line.ProductId, out var product))
            {
                return NotFound(new { message = $"Product {line.ProductId} not found." });
            }

            if (product.Stock < line.Quantity)
            {
                return UnprocessableEntity(new { message = $"Not enough stock for '{product.Name}' (only {product.Stock} left)." });
            }

            orderItems.Add(new OrderItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = product.Price,
                Quantity = line.Quantity
            });
        }

        var total = orderItems.Sum(i => i.UnitPrice * i.Quantity);
        if (user.Account.Balance < total)
        {
            return UnprocessableEntity(new { message = $"Insufficient balance: order total is {total} THB, wallet has {user.Account.Balance} THB." });
        }

        var order = new Order
        {
            UserId = user.UserId,
            TotalAmount = total,
            Status = OrderStatus.Pending,
            CreatedAt = DateTime.UtcNow,
            Items = orderItems
        };

        var takenStock = new List<(string ProductId, int Quantity)>();

        await using var transaction = await _db.Database.BeginTransactionAsync();
        try
        {
            _db.Orders.Add(order);
            user.Account.Balance -= total;
            await _db.SaveChangesAsync();

            foreach (var line in lines)
            {
                // Stock >= quantity in the filter so two buyers can't push stock below zero
                var filter = Builders<Product>.Filter.Eq(p => p.Id, line.ProductId)
                             & Builders<Product>.Filter.Gte(p => p.Stock, line.Quantity);
                var update = Builders<Product>.Update.Inc(p => p.Stock, -line.Quantity);

                var result = await _mongo.Products.UpdateOneAsync(filter, update);
                if (result.ModifiedCount == 0)
                {
                    await RestoreStockAsync(takenStock);
                    await transaction.RollbackAsync();
                    return Conflict(new { message = "Stock changed while placing the order, please try again." });
                }

                takenStock.Add(line);
            }

            order.Status = OrderStatus.Completed;
            await _db.SaveChangesAsync();
            await transaction.CommitAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            // another order changed this wallet at the same time
            await RestoreStockAsync(takenStock);
            return Conflict(new { message = "Wallet was updated by another order, please try again." });
        }
        catch
        {
            await RestoreStockAsync(takenStock);
            throw;
        }

        return CreatedAtAction(nameof(GetOrder), new { id = order.OrderId }, ToResponse(order));
    }

    // GET /api/v1/orders/{id}
    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderResponse>> GetOrder(int id)
    {
        var order = await _db.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.OrderId == id);

        if (order is null)
        {
            return NotFound(new { message = $"Order {id} not found." });
        }

        return Ok(ToResponse(order));
    }

    // Best effort: if Mongo itself is down this can fail too (see README, known limitations).
    private async Task RestoreStockAsync(List<(string ProductId, int Quantity)> takenStock)
    {
        foreach (var (productId, quantity) in takenStock)
        {
            try
            {
                await _mongo.Products.UpdateOneAsync(
                    Builders<Product>.Filter.Eq(p => p.Id, productId),
                    Builders<Product>.Update.Inc(p => p.Stock, quantity));
            }
            catch (MongoException)
            {
                // keep going so the other products still get their stock back
            }
        }
    }

    private static OrderResponse ToResponse(Order order) => new(
        order.OrderId,
        order.UserId,
        order.TotalAmount,
        order.Status.ToString(),
        order.CreatedAt,
        order.Items.Select(i => new OrderItemResponse(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity)).ToList());
}
