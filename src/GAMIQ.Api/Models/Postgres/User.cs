namespace GAMIQ.Api.Models.Postgres;

public class User
{
    public int UserId { get; set; }
    public string Username { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string PasswordHash { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Account? Account { get; set; }
    public ICollection<Order> Orders { get; set; } = new List<Order>();
}
