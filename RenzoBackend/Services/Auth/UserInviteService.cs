using RenzoBackend.Models.Database;
using System.Security.Cryptography;

namespace RenzoBackend.Services.Auth;

/// <summary>
/// Service for generating and managing user invite tokens.
/// </summary>
public class UserInviteService
{
    /// <summary>Lifetime of an invite / password-set link.</summary>
    public static readonly TimeSpan PasswordSetTokenLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// Generates a one-time password-set (invite) token. Stores only its SHA-256
    /// hash plus a 7-day expiry on the user; returns the RAW token for the invite
    /// link. Hardened to match the reset token: 256-bit crypto-random, hashed at
    /// rest, expiring, single-use, constant-time compared — the old form was a
    /// plaintext Guid with no expiry and a case-insensitive compare.
    /// </summary>
    public string GeneratePasswordSetToken(UserEntity user)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        user.PasswordSetToken = HashResetToken(token);
        user.PasswordSetTokenExpiresAt = DateTime.UtcNow.Add(PasswordSetTokenLifetime);
        return token;
    }

    /// <summary>
    /// Consumes (validates and clears) a password-set token: matches the stored
    /// hash (constant-time), checks expiry, and is single-use.
    /// </summary>
    public bool ConsumePasswordSetToken(UserEntity user, string token)
    {
        if (string.IsNullOrWhiteSpace(user.PasswordSetToken) ||
            user.PasswordSetTokenExpiresAt == null ||
            user.PasswordSetTokenExpiresAt.Value < DateTime.UtcNow ||
            string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        byte[] expected = Convert.FromHexString(user.PasswordSetToken);
        byte[] actual = Convert.FromHexString(HashResetToken(token));
        if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            return false;

        user.PasswordSetToken = null;
        user.PasswordSetTokenExpiresAt = null;
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

    /// <summary>
    /// The stored-hash form of a raw reset token. Public so the reset flow can
    /// look a user up BY their token (self-identifying reset) instead of trusting
    /// a username supplied alongside it — usernames are publicly visible on the
    /// user-select screen, so they must not gate account recovery.
    /// </summary>
    public static string HashResetToken(string token) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token.ToUpperInvariant())));

    /// <summary>
    /// Generates the formatted invite message text.
    /// </summary>
    /// <param name="user">The user being invited.</param>
    /// <param name="externalDomain">The external domain (e.g. https://renzo.example.com).</param>
    /// <param name="authEnabled">Whether authentication is enabled.</param>
    public string GetInviteMessage(UserEntity user, string externalDomain, bool authEnabled, string? rawSetToken = null)
    {
        string cleanDomain = externalDomain.TrimEnd('/');

        // rawSetToken is the un-hashed token from GeneratePasswordSetToken — the
        // stored user.PasswordSetToken is only its hash and must never be in a link.
        if (authEnabled && !string.IsNullOrWhiteSpace(rawSetToken))
        {
            return $"Hello {user.Username},\n\n" +
                   $"Click this link to set your password (valid for 7 days):\n" +
                   $"{cleanDomain}/auth/set-password?username={Uri.EscapeDataString(user.Username)}&token={rawSetToken}\n\n" +
                   $"Your OPDS path is: {cleanDomain}/{user.OpdsPath}";
        }
        else
        {
            return $"Hello {user.Username},\n\n" +
                   $"Your OPDS path is: {cleanDomain}/{user.OpdsPath}";
        }
    }
}