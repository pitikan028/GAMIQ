using GAMIQ.Api.Config;
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

        // CP2 Phase 2 (dual write): also fill the new columns. Both go out in the same INSERT as
        // TotalAmount, so an order can never have the old value without the new one.
        if (_db.Phase >= MigrationPhase.DualWrite)
        {
            order.PriceAmount = total;
            order.CurrencyCode = user.Account.Currency;
        }

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
    // CP3: projection. The order and its items come back in ONE SQL query (a LEFT JOIN), and
    // only the columns the answer needs are selected. No entity is built or tracked.
    [HttpGet("{id:int}")]
    public async Task<ActionResult<OrderResponse>> GetOrder(int id)
    {
        var row = await SelectOrderRows(_db.Orders.Where(o => o.OrderId == id)).FirstOrDefaultAsync();

        if (row is null)
        {
            return NotFound(new { message = $"Order {id} not found." });
        }

        return Ok(ToResponse(row));
    }

    // GET /api/v1/users/{userId}/orders?page=1&pageSize=20
    // CP3: the order history of one user, newest first. The number of SQL queries is the same
    // (3) no matter how many orders or items there are:
    //   1. does the user exist (so an unknown user is 404 and not an empty list)
    //   2. count the orders for paging
    //   3. the page itself, with the item count and quantity counted by Postgres in the same
    //      query. A loop that loads the items of every order one by one would be N+1.
    [HttpGet("~/api/v1/users/{userId:int}/orders")]
    public async Task<ActionResult<PagedResult<OrderSummaryResponse>>> GetUserOrders(
        int userId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);

        if (!await _db.Users.AnyAsync(u => u.UserId == userId))
        {
            return NotFound(new { message = $"User {userId} not found." });
        }

        var orders = _db.Orders.Where(o => o.UserId == userId);
        var totalCount = await orders.CountAsync();

        // newest first, uses the (UserId, CreatedAt) index; OrderId makes the order stable
        var pageQuery = orders
            .OrderByDescending(o => o.CreatedAt)
            .ThenByDescending(o => o.OrderId)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);

        // same CP2 rule as GetOrder: which amount column we may read depends on the phase
        var rows = _db.Phase >= MigrationPhase.ReadNew
            ? await pageQuery.Select(o => new OrderSummaryRow(
                o.OrderId, o.PriceAmount, o.CurrencyCode, o.Status, o.CreatedAt,
                o.Items.Count(), o.Items.Sum(i => i.Quantity))).ToListAsync()
            : await pageQuery.Select(o => new OrderSummaryRow(
                o.OrderId, o.TotalAmount, null, o.Status, o.CreatedAt,
                o.Items.Count(), o.Items.Sum(i => i.Quantity))).ToListAsync();

        var items = rows.Select(r => new OrderSummaryResponse(
            r.OrderId, AmountOf(r.OrderId, r.Amount), r.CurrencyCode, r.Status.ToString(),
            r.CreatedAt, r.ItemCount, r.TotalQuantity)).ToList();

        var totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
        return Ok(new PagedResult<OrderSummaryResponse>(items, page, pageSize, totalCount, totalPages));
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

    // CP2 Phase 4 (switch read): from ReadNew on, the amount in the answer comes from PriceAmount
    // and the currency from CurrencyCode. TotalAmount is not looked at anymore, so it can be
    // dropped in phase 5. The JSON field is still called totalAmount, so clients don't notice.
    // CP3: this is written as a projection, so EF only selects these columns. In Contract the
    // TotalAmount property is not even mapped, so the query must not mention it.
    private IQueryable<OrderRow> SelectOrderRows(IQueryable<Order> orders) =>
        _db.Phase >= MigrationPhase.ReadNew
            ? orders.Select(o => new OrderRow(
                o.OrderId, o.UserId, o.PriceAmount, o.CurrencyCode, o.Status, o.CreatedAt,
                o.Items.OrderBy(i => i.OrderItemId)
                    .Select(i => new OrderItemResponse(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity))
                    .ToList()))
            : orders.Select(o => new OrderRow(
                o.OrderId, o.UserId, o.TotalAmount, null, o.Status, o.CreatedAt,
                o.Items.OrderBy(i => i.OrderItemId)
                    .Select(i => new OrderItemResponse(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity))
                    .ToList()));

    // POST /orders already has the order in memory, so it builds the same row without a query
    private OrderResponse ToResponse(Order order)
    {
        var readNew = _db.Phase >= MigrationPhase.ReadNew;
        return ToResponse(new OrderRow(
            order.OrderId,
            order.UserId,
            readNew ? order.PriceAmount : order.TotalAmount,
            readNew ? order.CurrencyCode : null,
            order.Status,
            order.CreatedAt,
            order.Items.Select(i => new OrderItemResponse(i.ProductId, i.ProductName, i.UnitPrice, i.Quantity)).ToList()));
    }

    private static OrderResponse ToResponse(OrderRow row) => new(
        row.OrderId,
        row.UserId,
        AmountOf(row.OrderId, row.Amount),
        row.CurrencyCode,
        row.Status.ToString(),
        row.CreatedAt,
        row.Items);

    // No fallback to the other amount column on purpose. MigrationGuard only lets the API into
    // ReadNew after the backfill, so an empty amount would be a real bug.
    private static decimal AmountOf(int orderId, decimal? amount) =>
        amount ?? throw new InvalidOperationException($"Order {orderId} has no amount in the column this phase reads.");

    // what the projections select; Amount is PriceAmount or TotalAmount depending on the phase
    private record OrderRow(
        int OrderId, int UserId, decimal? Amount, string? CurrencyCode, OrderStatus Status, DateTime CreatedAt,
        List<OrderItemResponse> Items);

    private record OrderSummaryRow(
        int OrderId, decimal? Amount, string? CurrencyCode, OrderStatus Status, DateTime CreatedAt,
        int ItemCount, int TotalQuantity);
}
