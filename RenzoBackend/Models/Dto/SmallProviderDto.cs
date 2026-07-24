using System.Text.Json.Serialization;
using RenzoBackend.Models;

namespace RenzoBackend.Models.Dto;

public class SmallProviderDto : ProviderSummaryBase
{
    [JsonPropertyName("url")]
    public override string? Url { get; set; } = string.Empty;
}
