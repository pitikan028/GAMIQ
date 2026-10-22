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
    // CP3: projection. One SQL query that joins the wallet and selects only the columns of the
    // answer (plus the wallet version for the ETag). PasswordHash is never read from disk here,
    // and no entity is built or tracked.
    [HttpGet("{id:int}")]
    public async Task<ActionResult<UserResponse>> GetUser(int id)
    {
        var user = await _db.Users
            .Where(u => u.UserId == id)
            .Select(u => new
            {
                u.UserId,
                u.Username,
                u.Email,
                u.CreatedAt,
                Balance = (decimal?)u.Account!.Balance,
                Currency = u.Account!.Currency,
                Version = (uint?)u.Account!.Version
            })
            .FirstOrDefaultAsync();

        if (user is null)
        {
            return NotFound(new { message = $"User {id} not found." });
        }

        if (user.Version is not null)
        {
            Response.Headers.ETag = $"\"{user.Version}\"";
        }

        return Ok(new UserResponse(user.UserId, user.Username, user.Email, user.CreatedAt, user.Balance ?? 0, user.Currency ?? "THB"));
    }

    // POST /api/v1/users/{id}/wallet/topup
    // CP3 optimistic concurrency: the UPDATE only matches if the wallet's xmin is still the one we
    // read. If another request changed the wallet in between, nothing is written and the client
    // gets 409 and has to retry, so a top-up can never overwrite (lose) another one.
    // Optional If-Match: the client sends the ETag from GET /users/{id}. If the wallet changed since
    // then the answer is 412, also when the change happens between our read and our UPDATE.
    [HttpPost("{id:int}/wallet/topup")]
    public async Task<ActionResult<UserResponse>> TopUp(int id, TopUpRequest request)
    {
        if (decimal.Round(request.Amount, 2) != request.Amount)
        {
            return BadRequest(new { message = "Amount can have at most 2 decimals." });
        }

        var ifMatch = Request.Headers.IfMatch.ToString();
        uint? expectedVersion = null;
        if (!string.IsNullOrEmpty(ifMatch))
        {
            if (!TryParseETag(ifMatch, out var parsed))
            {
                return BadRequest(new { message = "If-Match must be the ETag from GET /users/{id}, for example \"12345\"." });
            }
            expectedVersion = parsed;
        }

        var user = await _db.Users.Include(u => u.Account).FirstOrDefaultAsync(u => u.UserId == id);
        if (user is null)
        {
            return NotFound(new { message = $"User {id} not found." });
        }

        if (user.Account is null)
        {
            return UnprocessableEntity(new { message = "User has no wallet account." });
        }

        if (expectedVersion is not null)
        {
            if (expectedVersion != user.Account.Version)
            {
                SetWalletETag(user.Account);
                return StatusCode(StatusCodes.Status412PreconditionFailed,
                    new { message = "Wallet was changed since you read it. Read it again (GET /users/{id}) and retry." });
            }

            // compare against the client's version in the UPDATE itself, not only in the check above
            _db.Entry(user.Account).Property(a => a.Version).OriginalValue = expectedVersion.Value;
        }

        user.Account.Balance += request.Amount;

        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (expectedVersion is not null)
            {
                return StatusCode(StatusCodes.Status412PreconditionFailed,
                    new { message = "Wallet was changed since you read it. Read it again (GET /users/{id}) and retry." });
            }
            return Conflict(new { message = "Wallet was updated by another request, please try again." });
        }

        // Npgsql reads the new xmin back after the UPDATE, so this is the version of the row we wrote
        SetWalletETag(user.Account);
        return Ok(ToResponse(user));
    }

    private void SetWalletETag(Account? account)
    {
        if (account is not null)
        {
            Response.Headers.ETag = $"\"{account.Version}\"";
        }
    }

    private static bool TryParseETag(string value, out uint version)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("W/"))
        {
            trimmed = trimmed[2..];
        }
        return uint.TryParse(trimmed.Trim('"'), out version);
    }

    private static UserResponse ToResponse(User user) =>
        new(user.UserId, user.Username, user.Email, user.CreatedAt, user.Account?.Balance ?? 0, user.Account?.Currency ?? "THB");
}
