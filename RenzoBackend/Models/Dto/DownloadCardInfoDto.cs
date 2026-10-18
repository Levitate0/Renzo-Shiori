using RenzoBackend.Models;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

public class DownloadCardInfoDto : DownloadSummaryBase
{
    [JsonPropertyName("pageCount")]
    public int PageCount { get; set; }

    [JsonPropertyName("chapterNumber")]
    public decimal? ChapterNumber { get; set; }

    [JsonPropertyName("chapterName")]
    public string ChapterName { get; set; } = string.Empty;

    /// <summary>The library series this download belongs to, so a live download can link to it.</summary>
    [JsonPropertyName("seriesId")]
    public Guid? SeriesId { get; set; }
}