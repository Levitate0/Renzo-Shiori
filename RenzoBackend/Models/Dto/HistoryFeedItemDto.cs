using System.Text.Json.Serialization;
using RenzoBackend.Models.Abstractions;

namespace RenzoBackend.Models.Dto;

/// <summary>
/// One chapter read, inside a stacked history entry.
/// </summary>
public class HistoryChapterDto
{
    [JsonPropertyName("chapterNumber")]
    public decimal? ChapterNumber { get; set; }

    [JsonPropertyName("chapterName")]
    public string? ChapterName { get; set; }

    /// <summary>Archive filename, so the row can open the reader directly.</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("readAt")]
    public DateTime ReadAt { get; set; }

    /// <summary>0..1. Below 1 means the chapter was left part-read.</summary>
    [JsonPropertyName("progress")]
    public float Progress { get; set; }

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }
}

/// <summary>
/// One entry in the reading-history feed.
///
/// A run of chapters read back-to-back from the same series collapses into a
/// SINGLE entry of kind "stack" carrying its chapters, rather than one entry
/// each — mirroring how the Updates feed stacks a batch release. The cap on the
/// feed counts stacks as one entry, so a binge cannot crowd everything else out
/// of the history.
/// </summary>
public class HistoryFeedItemDto : IThumb
{
    public const string KindChapter = "chapter";
    public const string KindStack = "stack";

    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("seriesTitle")]
    public string SeriesTitle { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    /// <summary>"chapter" or "stack".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindChapter;

    /// <summary>For a stack, the most recent read in it — what the feed sorts on.</summary>
    [JsonPropertyName("readAt")]
    public DateTime ReadAt { get; set; }

    // ── kind == "chapter" ──────────────────────────────────────────────
    [JsonPropertyName("chapterNumber")]
    public decimal? ChapterNumber { get; set; }

    [JsonPropertyName("chapterName")]
    public string? ChapterName { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("progress")]
    public float Progress { get; set; }

    [JsonPropertyName("completed")]
    public bool Completed { get; set; }

    // ── kind == "stack" ────────────────────────────────────────────────
    /// <summary>Chapters in the stack, highest chapter number first. Null for a single.</summary>
    [JsonPropertyName("chapters")]
    public List<HistoryChapterDto>? Chapters { get; set; }
}
