namespace GAMIQ.Api.Models.Postgres;

// One wallet per user, used to pay for orders.
public class Account
{
    public int AccountId { get; set; }
    public int UserId { get; set; }
    public decimal Balance { get; set; }
    public string Currency { get; set; } = "THB";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // mapped to postgres xmin, stops two orders from overwriting each other's balance update
    public uint Version { get; set; }

    public User User { get; set; } = null!;
}
