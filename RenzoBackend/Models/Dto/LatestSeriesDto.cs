using RenzoBackend.Models.Abstractions;
using RenzoBackend.Models.Enums;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

// [Schema] // Controller I/O Model
public class LatestSeriesDto : IThumb
{
    [Key]

    [JsonPropertyName("mihonId")]
    public string MihonId { get; set; }
    [JsonPropertyName("mihonProviderId")]
    public string? MihonProviderId { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;
    [JsonPropertyName("language")]
    public string Language { get; set; } = "";
    [JsonPropertyName("url")]
    public string? Url { get; set; }
    [JsonPropertyName("title")]
    public string Title { get; set; } = "";
    /// <summary>
    /// Computed 18+ detection: adult rating tags on this row, on any
    /// same-titled row from another source in the catalog, or on the linked
    /// library series (which aggregates all of its sources' tags).
    /// </summary>
    [JsonPropertyName("isNsfw")]
    public bool IsNsfw { get; set; }
    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; } = null;
    [JsonPropertyName("artist")]
    public string? Artist { get; set; } = null;
    [JsonPropertyName("author")]
    public string? Author { get; set; } = null;
    [JsonPropertyName("description")]
    public string? Description { get; set; } = null;
    [JsonPropertyName("genre")]
    public List<string> Genre { get; set; } = new();
    [JsonPropertyName("fetchDate")]
    public DateTime FetchDate { get; set; }
    [JsonPropertyName("chapterCount")]
    public long? ChapterCount { get; set; } = null;
    [JsonPropertyName("latestChapter")]
    public decimal? LatestChapter { get; set; }
    [JsonPropertyName("latestChapterTitle")]
    public string LatestChapterTitle { get; set; } = "";
    [JsonPropertyName("status")]
    public SeriesStatus Status { get; set; } = SeriesStatus.UNKNOWN;
    [JsonPropertyName("inLibrary")]
    public InLibraryStatus InLibrary { get; set; } = InLibraryStatus.NotInLibrary;
    [JsonPropertyName("seriesId")]
    public Guid? SeriesId { get; set; }

}