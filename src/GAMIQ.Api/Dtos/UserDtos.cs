using System.ComponentModel.DataAnnotations;

namespace GAMIQ.Api.Dtos;

public record CreateUserRequest(
    [Required, StringLength(50, MinimumLength = 3)] string Username,
    [Required, EmailAddress, StringLength(255)] string Email,
    [Required, MinLength(8)] string Password);

public record UserResponse(int UserId, string Username, string Email, DateTime CreatedAt, decimal Balance, string Currency);
