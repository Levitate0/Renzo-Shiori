using RensaioBackend.Models.Abstractions;
using System.Text.Json.Serialization;

namespace RensaioBackend.Models.Dto;

/// <summary>
/// One row of the "Updates" feed (Suwayomi-style): either a series that was
/// added to the library or a chapter that finished downloading.
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
}
