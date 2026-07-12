using System.ComponentModel.DataAnnotations;

namespace RensaioBackend.Models.Database;

/// <summary>
/// A user-defined favorites list ("Manhwa favourites", "Manga favourites #2", …).
/// One nesting level is allowed: a list with a <see cref="ParentId"/> is a
/// sub-list ("alt name") grouped under its parent tab.
/// </summary>
public class FavoriteListEntity
{
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// Owning user. <see cref="Guid.Empty"/> when no user context exists
    /// (auth disabled and no profile selected) — a shared bucket.
    /// </summary>
    [Required]
    public Guid UserId { get; set; }

    /// <summary>Parent list for sub-lists; null for top-level tabs.</summary>
    public Guid? ParentId { get; set; }

    [Required]
    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
