using RenzoBackend.Models.Abstractions;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models;

/// <summary>
/// Shared metadata for Mihon-linked series projections.
/// </summary>
public abstract class SeriesSummaryBase : IBridgeItemInfo, IThumb
{

    [JsonPropertyName("mihonId")]
    public string? MihonId { get; set; }

    [JsonPropertyName("mihonProviderId")]
    public string? MihonProviderId { get; set; }

    [JsonPropertyName("bridgeItemInfo")]
    public string? BridgeItemInfo { get; set; }

    [JsonPropertyName("provider")]
    public string Provider { get; set; } = string.Empty;

    [JsonPropertyName("lang")]
    public string Lang { get; set; } = string.Empty;

    [JsonPropertyName("thumbnailUrl")]
    public string? ThumbnailUrl { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("useCover")]
    public bool UseCover { get; set; }

    /// <summary>
    /// Designates this source as the series' status authority when adding it — the
    /// one source whose ONGOING/COMPLETED/etc. drives the series-level status.
    /// Single-select; the add flow normalizes to exactly one (falling back to the
    /// permanent source if none is designated).
    /// </summary>
    [JsonPropertyName("useStatus")]
    public bool UseStatus { get; set; }

    [JsonPropertyName("isStorage")]
    public bool IsStorage { get; set; }

    [JsonPropertyName("isLocal")]
    public bool IsLocal { get; set; }
}
