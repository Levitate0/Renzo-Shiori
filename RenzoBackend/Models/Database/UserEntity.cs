using RenzoBackend.Models.Enums;
using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Models.Database;

public class UserEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Avatar image stored as blob - uploaded directly or fetched from Gravatar by the frontend.
    /// </summary>
    public byte[]? AvatarBlob { get; set; }

    /// <summary>
    /// MIME type of the avatar, e.g. "image/png", "image/jpeg".
    /// </summary>
    public string? AvatarContentType { get; set; }

    // ── Per-user content preferences ────────────────────────────────────
    // NULL means "not set — inherit the server default", which is what makes
    // this safe to add to a live install: every existing user keeps behaving
    // exactly as the global setting says until they change something, and the
    // global value stays meaningful as the default for new accounts.
    // Background jobs that have no user still fall back to the same defaults.

    /// <summary>Comma-separated language codes, most preferred first. NULL = inherit.</summary>
    public string? PreferredLanguages { get; set; }

    /// <summary>Stored as the <c>NsfwVisibility</c> enum's int value. NULL = inherit.</summary>
    public int? NsfwVisibility { get; set; }

    /// <summary>NULL = inherit.</summary>
    public bool? DownloadAllChapters { get; set; }

    /// <summary>
    /// Nullable - users can exist without passwords when auth is disabled.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Cryptographic salt used for password hashing.
    /// </summary>
    public string? Salt { get; set; }

    /// <summary>
    /// SHA-256 hash of the one-time password-set (invite) token. Only the hash is
    /// stored — the raw token lives only in the invite link. Single-use + expiring
    /// (see PasswordSetTokenExpiresAt), like the reset token.
    /// </summary>
    public string? PasswordSetToken { get; set; }

    /// <summary>
    /// Expiration of the current password-set (invite) token.
    /// </summary>
    public DateTime? PasswordSetTokenExpiresAt { get; set; }

    /// <summary>
    /// Optional email address, used for self-service password reset.
    /// </summary>
    public string? Email { get; set; }

    /// <summary>
    /// SHA-256 hash of the current self-service password-reset token. Unlike
    /// PasswordSetToken (admin-generated, shown in the UI), reset tokens are
    /// only ever sent by email, so only the hash is stored.
    /// </summary>
    public string? PasswordResetTokenHash { get; set; }

    /// <summary>
    /// Expiration of the current password-reset token.
    /// </summary>
    public DateTime? PasswordResetExpiresAt { get; set; }

    /// <summary>
    /// SHA-256 hash of the raw refresh token for "Remember Me" functionality.
    /// </summary>
    public string? RefreshTokenHash { get; set; }

    /// <summary>
    /// Expiration of the current refresh token. Auto-bumped on every refresh.
    /// </summary>
    public DateTime? RefreshTokenExpiresAt { get; set; }

    [Required]
    public UserLevel Level { get; set; } = UserLevel.User;

    /// <summary>
    /// Unique OPDS access path, e.g. "feather-flood".
    /// Acts as a security-by-obscurity mechanism for OPDS access.
    /// </summary>
    [Required]
    public string OpdsPath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? LastLoginAt { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Per-user UI preferences as a small JSON blob (theme mode, accent color,
    /// custom accent HSL). Owned by the user; hydrated on login and saved via
    /// PUT /api/auth/me so appearance follows the account across devices.
    /// </summary>
    public string? Preferences { get; set; }
}