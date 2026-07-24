using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Models.Database;

/// <summary>
/// A source a specific user has enabled for their own Search/Browse/Add-series.
/// The underlying extension is a single shared install (one JVM bridge for every
/// user), but VISIBILITY is per-user: installing a source (any privilege level)
/// only makes it usable by the installer until another user separately enables
/// it for themselves too — no automatic inheritance across users.
/// </summary>
public class UserProviderEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    /// <summary>The source's MihonProviderId, e.g. "eu.kanade.tachiyomi.extension.en.comix|123...".</summary>
    [Required]
    public string MihonProviderId { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
