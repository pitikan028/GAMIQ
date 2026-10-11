namespace GAMIQ.Api.Models.Postgres;

// ProductId/ProductName are a snapshot of the MongoDB product at purchase time,
// since MongoDB documents can change independently of historical orders.
public class OrderItem
{
    public int OrderItemId { get; set; }
    public int OrderId { get; set; }
    public string ProductId { get; set; } = null!;
    public string ProductName { get; set; } = null!;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }

    public Order Order { get; set; } = null!;
}
