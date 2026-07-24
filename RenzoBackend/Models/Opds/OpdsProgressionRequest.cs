using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Opds;

public class OpdsProgressionRequest
{
    [JsonPropertyName("modified")]
    public DateTime? Modified { get; set; }

    [JsonPropertyName("device")]
    public OpdsProgressionDeviceDto? Device { get; set; }

    [JsonPropertyName("progression")]
    public double? Progression { get; set; }

    [JsonPropertyName("references")]
    public List<string>? References { get; set; }
}