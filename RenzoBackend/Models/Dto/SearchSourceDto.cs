using System.Text.Json.Serialization;
using RenzoBackend.Models;

namespace RenzoBackend.Models.Dto;

public class SearchSourceDto : ProviderSummaryBase
{
    [JsonPropertyName("mihonProviderId")]
    public string MihonProviderId { get; set; } = string.Empty;

}