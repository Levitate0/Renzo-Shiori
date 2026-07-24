using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

public class ExtensionSourceDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("lang")]
    public string Language { get; set; }
}
