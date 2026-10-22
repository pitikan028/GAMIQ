using System.ComponentModel.DataAnnotations;

namespace GAMIQ.Api.Dtos;

public record CreateUserRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, EmailAddress, StringLength(255)] string Email,
    [Required, MinLength(8)] string Password);

// CP3: money added to the wallet, at most 2 decimals (Balance is decimal(18,2))
public record TopUpRequest(
    [Range(typeof(decimal), "0.01", "100000", ParseLimitsInInvariantCulture = true, ConvertValueInInvariantCulture = true)] decimal Amount);

public record UserResponse(int UserId, string Username, string Email, DateTime CreatedAt, decimal Balance, string Currency);
