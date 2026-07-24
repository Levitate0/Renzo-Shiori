using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

public class ProviderMatchChapterDto
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = "";
    [JsonPropertyName("chapterName")]
    public string ChapterName { get; set; } = "";
    [JsonPropertyName("chapterNumber")]
    public decimal? ChapterNumber { get; set; }
    [JsonPropertyName("matchInfoId")]
    public Guid? MatchInfoId { get; set; }

}