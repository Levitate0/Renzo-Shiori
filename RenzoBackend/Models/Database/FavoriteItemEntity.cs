using System.ComponentModel.DataAnnotations;

namespace RenzoBackend.Models.Database;

/// <summary>
/// Membership of a library series in a favorites list.
/// </summary>
public class FavoriteItemEntity
{
    [Key]
    public Guid Id { get; set; }

    [Required]
    public Guid ListId { get; set; }

    [Required]
    public Guid SeriesId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}
