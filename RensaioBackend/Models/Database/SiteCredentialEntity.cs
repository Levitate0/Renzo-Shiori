using System.ComponentModel.DataAnnotations;

namespace RensaioBackend.Models.Database;

/// <summary>
/// A user's login for a coin/paid scanlation site (EZmanga, Asura, …). The
/// password is stored encrypted (ASP.NET DataProtection); the harvested session
/// cookies are cached so a restart doesn't force an immediate re-login. Both are
/// injected into the Mihon shared cookie jar so the source's extension serves
/// chapters the user has paid for.
/// </summary>
public class SiteCredentialEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>Owning user. <see cref="Guid.Empty"/> when auth is disabled / no profile selected.</summary>
    [Required]
    public Guid UserId { get; set; }

    /// <summary>Mihon source display name this login is for, e.g. "EZmanga".</summary>
    [Required]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Login username / email.</summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>Password, DataProtection-encrypted. Never returned to the client.</summary>
    public string? EncryptedPassword { get; set; }

    /// <summary>
    /// Last harvested cookies for this site, DataProtection-encrypted, serialized
    /// as domain-scoped Set-Cookie lines. Re-injected into the jar on startup.
    /// </summary>
    public string? EncryptedCookies { get; set; }

    public DateTime? LastLoginAt { get; set; }

    /// <summary>"ok" | "needs_login" | "failed" | "manual_cookie" — drives the status badge.</summary>
    public string Status { get; set; } = "needs_login";

    /// <summary>Short human-readable detail for the last login attempt (error text, etc.).</summary>
    public string? StatusDetail { get; set; }
}
