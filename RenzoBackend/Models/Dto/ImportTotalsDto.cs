using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto
{
    public class ImportTotalsDto
    {
        [JsonPropertyName("totalSeries")]
        public int TotalSeries { get; set; }
        [JsonPropertyName("totalProviders")]
        public int TotalProviders { get; set; }
        [JsonPropertyName("totalDownloads")]
        public int TotalDownloads { get; set; }
    }
}