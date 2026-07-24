using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

public class UserDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("avatarBase64")]
    public string? AvatarBase64 { get; set; }

    [JsonPropertyName("avatarContentType")]
    public string? AvatarContentType { get; set; }

    [JsonPropertyName("level")]
    public UserLevel Level { get; set; }

    [JsonPropertyName("opdsPath")]
    public string OpdsPath { get; set; } = string.Empty;

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; set; }

    [JsonPropertyName("lastLoginAt")]
    public DateTime? LastLoginAt { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("hasPassword")]
    public bool HasPassword { get; set; }

    [JsonPropertyName("email")]
    public string? Email { get; set; }

    public static UserDto FromEntity(UserEntity entity)
    {
        return new UserDto
        {
            Id = entity.Id,
            Username = entity.Username,
            AvatarBase64 = entity.AvatarBlob != null ? Convert.ToBase64String(entity.AvatarBlob) : null,
            AvatarContentType = entity.AvatarContentType,
            Level = entity.Level,
            OpdsPath = entity.OpdsPath,
            CreatedAt = entity.CreatedAt,
            LastLoginAt = entity.LastLoginAt,
            IsActive = entity.IsActive,
            HasPassword = !string.IsNullOrWhiteSpace(entity.PasswordHash),
            Email = entity.Email
        };
    }
}

public class CreateUserDto
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("level")]
    public UserLevel Level { get; set; } = UserLevel.User;
}

public class UpdateUserDto
{
    [JsonPropertyName("avatarBase64")]
    public string? AvatarBase64 { get; set; }

    [JsonPropertyName("avatarContentType")]
    public string? AvatarContentType { get; set; }

    [JsonPropertyName("removeAvatar")]
    public bool? RemoveAvatar { get; set; }

    [JsonPropertyName("level")]
    public UserLevel? Level { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }

    /// <summary>
    /// New email address. Empty string clears it; null leaves it unchanged.
    /// </summary>
    [JsonPropertyName("email")]
    public string? Email { get; set; }
}

public class AuthStatusDto
{
    [JsonPropertyName("authenticationEnabled")]
    public bool AuthenticationEnabled { get; set; }

    [JsonPropertyName("hasUsers")]
    public bool HasUsers { get; set; }

    [JsonPropertyName("users")]
    public List<UserDto>? Users { get; set; }
}

public class LoginRequestDto
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("rememberMe")]
    public bool RememberMe { get; set; }
}

public class LoginResponseDto
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("user")]
    public UserDto User { get; set; } = null!;
}

public class SelectUserRequestDto
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;
}

public class SetPasswordRequestDto
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;
}

public class ChangePasswordDto
{
    [JsonPropertyName("currentPassword")]
    public string CurrentPassword { get; set; } = string.Empty;

    [JsonPropertyName("newPassword")]
    public string NewPassword { get; set; } = string.Empty;
}

public class ForgotPasswordRequestDto
{
    /// <summary>
    /// Email address of the account to reset. Recovery is keyed strictly on
    /// email — never username (which is publicly visible on the user-select
    /// screen, so allowing it would let anyone trigger a reset for any account).
    /// </summary>
    [JsonPropertyName("email")]
    public string Email { get; set; } = string.Empty;
}

public class ResetPasswordRequestDto
{
    /// <summary>
    /// The emailed reset token. Self-identifying: the account is resolved from
    /// this token alone, with no username in the link or request.
    /// </summary>
    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("newPassword")]
    public string NewPassword { get; set; } = string.Empty;
}

public class InviteMessageDto
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string Token { get; set; } = string.Empty;

    [JsonPropertyName("opdsPath")]
    public string OpdsPath { get; set; } = string.Empty;
}

public class RegenerateOpdsResponseDto
{
    [JsonPropertyName("opdsPath")]
    public string OpdsPath { get; set; } = string.Empty;
}