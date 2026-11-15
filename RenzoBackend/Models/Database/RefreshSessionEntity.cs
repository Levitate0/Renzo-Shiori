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

    /// <summary>
    /// Hash of the token this session's current one replaced, still accepted
    /// until <see cref="PreviousTokenValidUntil"/>.
    ///
    /// Rotation assumes the client receives the replacement, and on mobile it
    /// often does not: a phone handing off between towers, or waking up on a
    /// network that is still associating, loses the response to a refresh the
    /// server has already committed. The device is then holding a token the
    /// server discarded, through no fault of its own, and its next refresh is a
    /// hard sign-out.
    ///
    /// Persisted rather than held in memory because the retry is not prompt —
    /// the device may not come back until the user next opens the app, hours
    /// later, and a server restart in between must not cost them the session.
    /// </summary>
    public string? PreviousTokenHash { get; set; }

    public DateTime? PreviousTokenValidUntil { get; set; }

    /// <summary>
    /// Set when <see cref="PreviousTokenHash"/> has already been forgiven once
    /// (the device came back on it and was issued a fresh token). A second
    /// arrival on the same old token is no longer "a lost response" — it is a
    /// copy of the token in someone else's hands, and the session is revoked.
    /// </summary>
    public DateTime? PreviousTokenReplayedAt { get; set; }

    /// <summary>
    /// Hash of a token this session has moved past for good: the one before
    /// <see cref="PreviousTokenHash"/>, or a replacement superseded by the
    /// stranded-device path. A legitimate client never presents it again, so
    /// seeing it means the session's tokens have been copied — revoke.
    /// A newline-separated LIST (recent rolled-off hashes plus pinned disowned
    /// replacements), not one slot: a single overwritten slot was wiped by the
    /// copier's own next rotation. Format in RefreshSessionService.RetiredEntries.
    /// </summary>
    public string? RetiredTokenHash { get; set; }

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
