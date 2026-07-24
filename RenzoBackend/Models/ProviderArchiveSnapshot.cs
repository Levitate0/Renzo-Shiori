using System.Text.Json.Serialization;

namespace RenzoBackend.Models;

public class ProviderArchiveSnapshot : ChapterDescriptorBase
{
    [JsonPropertyName("archiveName")]
    public required string ArchiveName { get; set; }

    [JsonPropertyName("creationDate")]
    public DateTime? CreationDate { get; set; }
}
