using System.Text.Json.Serialization;
using RenzoBackend.Models;

namespace RenzoBackend.Models.Dto;

public class MatchInfoDto : ProviderSummaryBase
{
    [JsonPropertyName("id")]
    public Guid Id { get; set; }
}