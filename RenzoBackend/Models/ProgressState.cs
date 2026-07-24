using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models;

public class ProgressState
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("jobType")]
    public JobType JobType { get; set; }

    [JsonPropertyName("download")]
    public DownloadCardInfoDto? Download { get; set; }

    [JsonPropertyName("progressStatus")]
    public ProgressStatus ProgressStatus { get; set; }

    [JsonPropertyName("percentage")]
    public decimal Percentage { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// The series owner this progress event belongs to (Download jobs only) —
    /// used server-side to route the SignalR broadcast to only that user (plus
    /// Owner-level accounts). Never sent to clients: other users' download
    /// activity, thumbnails and chapter titles shouldn't be visible to you just
    /// because you're both connected to the same hub.
    /// </summary>
    [JsonIgnore]
    public Guid? OwnerId { get; set; }
}