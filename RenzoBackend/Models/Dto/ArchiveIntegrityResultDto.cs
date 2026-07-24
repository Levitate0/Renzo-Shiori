using RenzoBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

public class ArchiveIntegrityResultDto
{
    [JsonPropertyName("result")]
    public ArchiveResult Result { get; set; }
    [JsonPropertyName("filename")]

    public string Filename { get; set; } = "";
}