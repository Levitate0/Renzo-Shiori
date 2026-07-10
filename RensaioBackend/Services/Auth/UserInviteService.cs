using RensaioBackend.Models.Database;
using System.Security.Cryptography;

namespace RensaioBackend.Services.Auth;

/// <summary>
/// Service for generating and managing user invite tokens.
/// </summary>
public class UserInviteService
{
    /// <summary>
    /// Generates a one-time password set token for a user.
    /// Stores the token in the user entity.
    /// </summary>
    public string GeneratePasswordSetToken(UserEntity user)
    {
        string token = Guid.NewGuid().ToString("N");
        user.PasswordSetToken = token;
        return token;
    }

    /// <summary>
    /// Consumes (validates and clears) a password set token.
    /// Returns true if the token was valid and consumed.
    /// </summary>
    public bool ConsumePasswordSetToken(UserEntity user, string token)
    {
        if (string.IsNullOrWhiteSpace(user.PasswordSetToken))
            return false;

        if (!string.Equals(user.PasswordSetToken, token, StringComparison.OrdinalIgnoreCase))
            return false;

        user.PasswordSetToken = null;
        return true;
    }

    /// <summary>
    /// Lifetime of an emailed password-reset link.
    /// </summary>
    public static readonly TimeSpan PasswordResetTokenLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// Generates a self-service password-reset token, storing only its SHA-256
    /// hash plus an expiry on the user. Returns the raw token for the email
    /// link; it is never persisted or shown in any UI.
    /// </summary>
    public string GeneratePasswordResetToken(UserEntity user)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        user.PasswordResetTokenHash = HashResetToken(token);
        user.PasswordResetExpiresAt = DateTime.UtcNow.Add(PasswordResetTokenLifetime);
        return token;
    }

    /// <summary>
    /// Consumes (validates and clears) a password-reset token.
    /// Returns true if the token matched the stored hash and had not expired.
    /// </summary>
    public bool ConsumePasswordResetToken(UserEntity user, string token)
    {
        if (string.IsNullOrWhiteSpace(user.PasswordResetTokenHash) ||
            user.PasswordResetExpiresAt == null ||
            user.PasswordResetExpiresAt.Value < DateTime.UtcNow ||
            string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] expected = Convert.FromHexString(user.PasswordResetTokenHash);
        byte[] actual = Convert.FromHexString(HashResetToken(token));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            return false;

        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;
        return true;
    }

    private static string HashResetToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token.ToUpperInvariant())));

    /// <summary>
    /// Generates the formatted invite message text.
    /// </summary>
    /// <param name="user">The user being invited.</param>
    /// <param name="externalDomain">The external domain (e.g. https://rensaio.example.com).</param>
    /// <param name="authEnabled">Whether authentication is enabled.</param>
    public string GetInviteMessage(UserEntity user, string externalDomain, bool authEnabled)
    {
        string cleanDomain = externalDomain.TrimEnd('/');

        if (authEnabled && !string.IsNullOrWhiteSpace(user.PasswordSetToken))
        {
            return $"Hello {user.Username},\n\n" +
                   $"Click this link to set your password:\n" +
                   $"{cleanDomain}/auth/set-password?username={Uri.EscapeDataString(user.Username)}&token={user.PasswordSetToken}\n\n" +
                   $"Your OPDS path is: {cleanDomain}/{user.OpdsPath}";
        }
        else
        {
            return $"Hello {user.Username},\n\n" +
                   $"Your OPDS path is: {cleanDomain}/{user.OpdsPath}";
        }
    }
}