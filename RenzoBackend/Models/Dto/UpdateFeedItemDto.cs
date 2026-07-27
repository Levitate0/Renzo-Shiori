using RenzoBackend.Models.Abstractions;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

/// <summary>
/// One row of the "Updates" feed (Suwayomi-style): either a series that was
/// added to the library, or a chapter release. For a chapter, <see cref="Timestamp"/>
/// is the source publish date (when it went up on the site), not a download time.
/// </summary>
public class UpdateFeedItemDto : IThumb
{
    public const string KindSeriesAdded = "seriesAdded";
    public const string KindNewChapter = "newChapter";

    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("seriesTitle")]
    public string SeriesTitle { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    /// <summary>"seriesAdded" or "newChapter".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindNewChapter;

    [JsonPropertyName("chapterNumber")]
    public decimal? ChapterNumber { get; set; }

    [JsonPropertyName("chapterName")]
    public string? ChapterName { get; set; }

    [JsonPropertyName("provider")]
    public string? Provider { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; }

    /// <summary>True when the requesting user has finished this chapter (newChapter items only).</summary>
    [JsonPropertyName("read")]
    public bool Read { get; set; }
}
