using RenzoBackend.Models.Abstractions;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

/// <summary>
/// What the Hub's release notifications poll (GET /api/serie/release-heads):
/// the highest chapter number of every series the caller can see, plus the
/// chapters found since the caller last asked.
///
/// Deciding what counts as a NEW RELEASE is left to the client on purpose. It
/// keeps the highest number it has seen per series and only announces a
/// chapter above that, so a chapter another source re-adds, or a backup source
/// fills in, can never announce itself however its rows were shuffled here: a
/// source being disabled prunes its rows, and the replacement's "found" time
/// then reads as now.
/// </summary>
public class ReleaseHeadsDto
{
    /// <summary>Server time of this answer; the client's next <c>since</c>.</summary>
    [JsonPropertyName("now")]
    public DateTime Now { get; set; }

    [JsonPropertyName("series")]
    public List<ReleaseHeadDto> Series { get; set; } = [];

    /// <summary>One per series and chapter number, found at or after <c>since</c>. Empty without it.</summary>
    [JsonPropertyName("fresh")]
    public List<FreshChapterDto> Fresh { get; set; } = [];
}

public class ReleaseHeadDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }

    /// <summary>Highest chapter number on any source, the library grid's "last chapter".</summary>
    [JsonPropertyName("last")]
    public decimal? Last { get; set; }
}

public class FreshChapterDto : IThumb
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("seriesTitle")]
    public string SeriesTitle { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    [JsonPropertyName("isNsfw")]
    public bool IsNsfw { get; set; }

    [JsonPropertyName("chapterNumber")]
    public decimal ChapterNumber { get; set; }

    [JsonPropertyName("chapterName")]
    public string? ChapterName { get; set; }

    /// <summary>When the update scan first found this chapter on any source.</summary>
    [JsonPropertyName("found")]
    public DateTime Found { get; set; }

    /// <summary>The earliest real publish date any source gives, or null when none does.</summary>
    [JsonPropertyName("published")]
    public DateTime? Published { get; set; }
}
