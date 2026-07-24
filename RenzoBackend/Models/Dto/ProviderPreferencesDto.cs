using RenzoBackend.Models;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

public class ProviderPreferencesDto : ProviderSummaryBase
{
    [JsonPropertyName("pkgName")]
    public required string PkgName { get; set; }
    [JsonPropertyName("preferences")]
    public List<ProviderPreferenceDto> Preferences { get; set; } = [];
}