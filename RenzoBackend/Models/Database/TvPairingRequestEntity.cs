using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Models.Database;

/// <summary>
/// A pending TV pairing request — the OAuth device-authorisation shape.
///
/// The TV displays <see cref="UserCode"/> and keeps its device code secret;
/// only the requesting device knows both, and that separation is the entire
/// security model. The device code is stored hashed because it is a bearer
/// secret: anyone holding it can claim the approved session.
///
/// Rows are short-lived (~10 minutes) and swept, both to keep the table small
/// and to shrink the window for guessing a user code.
/// </summary>
public class TvPairingRequestEntity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Short, human-readable, read off a screen across a room.</summary>
    public string UserCode { get; set; } = string.Empty;

    /// <summary>Hash of the device code the TV holds — never stored raw.</summary>
    public string DeviceCodeHash { get; set; } = string.Empty;

    /// <summary>Supplied by the device, shown on the approval page.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Where the request came from, shown on the approval page.</summary>
    public string? RequestIp { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime ExpiresAt { get; set; }

    /// <summary>Pending / Approved / Denied — see <see cref="TvPairingStatus"/>.</summary>
    public int Status { get; set; }

    /// <summary>The approver. Their identity is what the device is granted.</summary>
    public Guid? ApprovedUserId { get; set; }

    /// <summary>
    /// Failed approval attempts against this code. A short code is guessable,
    /// so the request locks itself after a handful of misses.
    /// </summary>
    public int FailedAttempts { get; set; }

    /// <summary>
    /// Set once the device has collected its session. A device code is
    /// single-use: a replay must not mint a second session.
    /// </summary>
    public bool Claimed { get; set; }
}

public static class TvPairingStatus
{
    public const int Pending = 0;
    public const int Approved = 1;
    public const int Denied = 2;
}
