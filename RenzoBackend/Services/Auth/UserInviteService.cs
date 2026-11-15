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
    /// Marks a stored password-set token as an explicit reset. Stored in front of
    /// the hash so no schema change is needed; hashes are hex, so it can't clash.
    /// </summary>
    private const string ResetMarker = "R:";

    /// <summary>
    /// Tags the stored token with the level the account had when it was issued
    /// ("L2:"). The issuing request checks the level and then saves the token, so
    /// an Owner promoting the account in between (which wipes pending tokens) could
    /// otherwise be followed by the save, leaving an Admin holding an invite that a
    /// non-owner Admin issued. A level change since issue voids the token.
    /// Untagged tokens from before this are still accepted.
    /// </summary>
    private const string LevelMarker = "L";

    /// <summary>
    /// Generates a one-time password-set (invite) token. Stores only its SHA-256
    /// hash plus a 7-day expiry on the user; returns the RAW token for the invite
    /// link. Hardened to match the reset token: 256-bit crypto-random, hashed at
    /// rest, expiring, single-use, constant-time compared — the old form was a
    /// plaintext Guid with no expiry and a case-insensitive compare.
    /// <paramref name="isReset"/> marks it as a deliberate reset of an account
    /// that already has a password (see <see cref="ConsumePasswordSetToken"/>);
    /// the caller has already checked it may reset that account.
    /// </summary>
    public string GeneratePasswordSetToken(UserEntity user, bool isReset = false)
    {
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        user.PasswordSetToken = (isReset ? ResetMarker : "") + LevelMarker + (int)user.Level + ":" + HashResetToken(token);
        user.PasswordSetTokenExpiresAt = DateTime.UtcNow.Add(PasswordSetTokenLifetime);
        return token;
    }

    /// <summary>
    /// Consumes (validates and clears) a password-set token: matches the stored
    /// hash (constant-time), checks expiry, and is single-use. A plain invite
    /// token only ever sets a FIRST password: once the account has a password
    /// (set any other way after the invite went out), only a token issued as a
    /// reset can replace it — otherwise an old invite link, which the inviting
    /// admin has seen, would stay a way into the account.
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

        string stored = user.PasswordSetToken;
        bool isReset = stored.StartsWith(ResetMarker, StringComparison.Ordinal);
        if (isReset)
            stored = stored[ResetMarker.Length..];
        else if (!string.IsNullOrWhiteSpace(user.PasswordHash))
            return false;
        if (stored.StartsWith(LevelMarker, StringComparison.Ordinal))
        {
            int colon = stored.IndexOf(':');
            if (colon < 0 || !int.TryParse(stored.AsSpan(LevelMarker.Length, colon - LevelMarker.Length), out int issuedFor)
                || issuedFor != (int)user.Level)
                return false;
            stored = stored[(colon + 1)..];
        }

        byte[] expected = Convert.FromHexString(stored);
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
                   // Not the OPDS path itself: it is this user's credential and the
                   // inviting admin reads this message. It is on their profile.
                   $"Your OPDS link is on your profile once you've signed in.";
        }
        else
        {
            // Auth off: there is no sign-in to protect (anyone can pick any user and
            // gets that user's path from select-user), and the path is all an
            // invite is for, so it stays in the message.
            return $"Hello {user.Username},\n\n" +
                   $"Your OPDS path is: {cleanDomain}/{user.OpdsPath}";
        }
    }
}