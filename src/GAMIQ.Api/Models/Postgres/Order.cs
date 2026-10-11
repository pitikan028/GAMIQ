namespace GAMIQ.Api.Models.Postgres;

public enum OrderStatus
{
    Pending,
    Completed,
    Failed,
    Cancelled
}

public class Order
{
    public int OrderId { get; set; }
    public int UserId { get; set; }
    // legacy: the order total, always THB because there was no currency column.
    // Nullable in C# only for the contract phase: orders made in Contract have NULL here until
    // the column is dropped, and ReadNew must still be able to load them if we roll back.
    public decimal? TotalAmount { get; set; }

    // CP2 new fields that replace TotalAmount. Nullable because old orders don't have them
    // until the backfill has run.
    public decimal? PriceAmount { get; set; }
    public string? CurrencyCode { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.Pending;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public User User { get; set; } = null!;
    public ICollection<OrderItem> Items { get; set; } = new List<OrderItem>();
}
