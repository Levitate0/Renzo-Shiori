using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Models.Database;

/// <summary>
/// A user's own saved value for one extension preference (e.g. MangaDex's
/// content-rating filter) on a shared, single-install source. The underlying
/// extension only has one live preference file, so this is a "save-and-apply"
/// model: saving writes your value into the shared file for immediate use, and
/// it's re-applied whenever you act on this source again and the live value has
/// drifted (e.g. another user saved their own since). Not a source of truth for
/// concurrent divergent values — see extension-resilience-style docs for why.
/// </summary>
public class UserProviderPreferenceEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid UserId { get; set; }

    /// <summary>Extension package name, e.g. "eu.kanade.tachiyomi.extension.all.mangadex".</summary>
    [Required]
    public string PkgName { get; set; } = string.Empty;

    /// <summary>The preference's Index within that package's preference list.</summary>
    public int PreferenceIndex { get; set; }

    /// <summary>Raw serialized CurrentValue (string/bool/string[] per ValueType), as JSON.</summary>
    public string ValueJson { get; set; } = string.Empty;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
