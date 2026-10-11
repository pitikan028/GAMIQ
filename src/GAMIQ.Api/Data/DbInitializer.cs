using Bogus;
using GAMIQ.Api.Models.Mongo;
using GAMIQ.Api.Models.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace GAMIQ.Api.Data;

// Applies migrations and seeds Users / Accounts / Orders / OrderItems.
public static class DbInitializer
{
    private const int UserCount = 1000;
    private const int OrderCount = 500;

    public static async Task SeedAsync(AppDbContext context, List<Product> products)
    {
        await context.Database.MigrateAsync();

        if (await context.Users.AnyAsync())
        {
            return;
        }

        var f = new Faker { Random = new Randomizer(42) };
        var hasher = new PasswordHasher<User>();

        // hashing is slow, so hash once and reuse it - every seeded user logs in with Passw0rd!
        var passwordHash = hasher.HashPassword(new User(), "Passw0rd!");

        var usernames = new HashSet<string>();
        var users = new List<User>();

        while (users.Count < UserCount)
        {
            var first = f.Name.FirstName();
            var last = f.Name.LastName();
            var username = $"{first}.{last}{f.Random.Number(1, 99)}".ToLowerInvariant();

            if (!usernames.Add(username))
            {
                continue;
            }

            var createdAt = f.Date.Past(2).ToUniversalTime();
            users.Add(new User
            {
                Username = username,
                Email = $"{username}@{f.PickRandom("gmail.com", "hotmail.com", "outlook.com", "cmu.ac.th")}",
                PasswordHash = passwordHash,
                CreatedAt = createdAt,
                Account = new Account
                {
                    Balance = Math.Round(f.Random.Decimal(0, 5000), 2),
                    Currency = "THB",
                    CreatedAt = createdAt
                }
            });
        }

        context.Users.AddRange(users);
        await context.SaveChangesAsync();

        if (products.Count == 0)
        {
            return;
        }

        var orders = new List<Order>();

        for (var i = 0; i < OrderCount; i++)
        {
            var user = f.PickRandom(users);
            var items = f.PickRandom(products, f.Random.Number(1, 3))
                .Select(p => new OrderItem
                {
                    ProductId = p.Id,
                    ProductName = p.Name,
                    UnitPrice = p.Price,
                    Quantity = f.Random.Number(1, 2)
                })
                .ToList();

            var total = items.Sum(it => it.UnitPrice * it.Quantity);

            // CP2: both the old and the new fields are set. EF only writes the ones that are
            // mapped in the current phase, so in Legacy these are still "old" orders with empty
            // new columns (the data for the backfill).
            orders.Add(new Order
            {
                UserId = user.UserId,
                TotalAmount = total,
                PriceAmount = total,
                CurrencyCode = "THB",
                Status = f.Random.WeightedRandom(
                    new[] { OrderStatus.Completed, OrderStatus.Cancelled, OrderStatus.Failed },
                    new[] { 0.88f, 0.08f, 0.04f }),
                CreatedAt = f.Date.Between(user.CreatedAt, DateTime.UtcNow).ToUniversalTime(),
                Items = items
            });
        }

        context.Orders.AddRange(orders);
        await context.SaveChangesAsync();
    }
}
