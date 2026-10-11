using GAMIQ.Api.Data;
using GAMIQ.Api.Dtos;
using GAMIQ.Api.Models.Postgres;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace GAMIQ.Api.Controllers;

[ApiController]
[Route("api/v1/users")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly PasswordHasher<User> _passwordHasher = new();

    public UsersController(AppDbContext db)
    {
        _db = db;
    }

    // POST /api/v1/users
    [HttpPost]
    public async Task<ActionResult<UserResponse>> CreateUser(CreateUserRequest request)
    {
        var username = request.Username.Trim().ToLowerInvariant();
        var email = request.Email.Trim().ToLowerInvariant();

        if (await _db.Users.AnyAsync(u => u.Username == username || u.Email == email))
        {
            return Conflict(new { message = "Username or email already exists." });
        }

        var user = new User
        {
            Username = username,
            Email = email,
            CreatedAt = DateTime.UtcNow,
            Account = new Account { Balance = 0, Currency = "THB", CreatedAt = DateTime.UtcNow }
        };
        user.PasswordHash = _passwordHasher.HashPassword(user, request.Password);

        _db.Users.Add(user);

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // two requests with the same username at the same time
            return Conflict(new { message = "Username or email already exists." });
        }

        return CreatedAtAction(nameof(GetUser), new { id = user.UserId }, ToResponse(user));
    }

    // GET /api/v1/users/{id}
    [HttpGet("{id:int}")]
    public async Task<ActionResult<UserResponse>> GetUser(int id)
    {
        var user = await _db.Users
            .AsNoTracking()
            .Include(u => u.Account)
            .FirstOrDefaultAsync(u => u.UserId == id);

        if (user is null)
        {
            return NotFound(new { message = $"User {id} not found." });
        }

        return Ok(ToResponse(user));
    }

    private static UserResponse ToResponse(User user) =>
        new(user.UserId, user.Username, user.Email, user.CreatedAt, user.Account?.Balance ?? 0, user.Account?.Currency ?? "THB");
}
