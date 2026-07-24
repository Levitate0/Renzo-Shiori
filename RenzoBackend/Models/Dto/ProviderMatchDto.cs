using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto
{
    public class ProviderMatchDto
    {
        [JsonPropertyName("id")]
        public Guid Id { get; set; }
        [JsonPropertyName("matchInfos")]
        public List<MatchInfoDto> MatchInfos { get; set; } = [];
        [JsonPropertyName("chapters")]
        public List<ProviderMatchChapterDto> Chapters { get; set; } = [];
    }
}
