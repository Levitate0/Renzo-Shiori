using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Models.Database;

/// <summary>
/// One remembered sign-in on one device.
///
/// Replaces the single <c>UserEntity.RefreshTokenHash</c> column, which allowed
/// exactly ONE remember-me session per account: signing in on a phone silently
/// invalidated the desktop's, and a paired TV would be evicted by the next
/// login anywhere else — which would have made TV pairing useless in practice.
///
/// A row is a device. Rotation updates the hash in place, so the device keeps
/// its identity (name, paired-at) across refreshes, and revoking one device
/// leaves the others signed in.
/// </summary>
public class RefreshSessionEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>Hash of the raw refresh token — never store the token itself.</summary>
    public string TokenHash { get; set; } = string.Empty;

    public DateTime ExpiresAt { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Bumped on every successful refresh, so "last seen" is real.</summary>
    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;

    /// <summary>Set on revoke; the row is kept so the UI can show what happened.</summary>
    public DateTime? RevokedAt { get; set; }

    /// <summary>What the user sees in their device list ("Living Room TV").</summary>
    public string? DeviceName { get; set; }

    /// <summary>IP that created the session — shown when approving a TV.</summary>
    public string? CreatedIp { get; set; }

    /// <summary>
    /// Paired through the TV flow rather than a typed password. Only used for
    /// labelling — a paired session is otherwise an ordinary session.
    /// </summary>
    public bool IsTvPairing { get; set; }

    public UserEntity? User { get; set; }
}
