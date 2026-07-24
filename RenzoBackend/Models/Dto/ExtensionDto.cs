using RenzoBackend.Models.Abstractions;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;
/// <summary>
/// Model class to represent an extension
/// </summary>
public class ExtensionDto : IThumb
{
    [JsonPropertyName("package")]
    public string Package { get; set; }
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
    [JsonPropertyName("thumbnailUrl")]
    public string ThumbnailUrl { get; set; }
    [JsonPropertyName("isStorage")]
    public bool IsStorage { get; set; } = true;
    [JsonPropertyName("isEnabled")]
    public bool IsEnabled { get; set; } = true;
    [JsonPropertyName("isBroken")]
    public bool IsBroken { get; set; } = false;
    [JsonPropertyName("isDead")]
    public bool IsDead { get; set; } = false;
    [JsonPropertyName("isInstaled")]
    public bool IsInstaled { get; set; } = false;
    /// <summary>
    /// True when the REQUESTING user has this source enabled for their own
    /// Search/Browse/Add-series — independent of whether it's installed
    /// system-wide by someone else. Drives the "Install" vs "Installed" state in
    /// the UI; a source installed by another user but not yet enabled by this one
    /// still shows as available to "Install" (which just enables it for them,
    /// cheaply, instead of re-fetching the APK).
    /// </summary>
    [JsonPropertyName("isEnabledForMe")]
    public bool IsEnabledForMe { get; set; } = false;
    [JsonPropertyName("activeEntry")]
    public int ActiveEntry { get; set; }
    [JsonPropertyName("autoUpdate")]
    public bool AutoUpdate { get; set; } = true;
    [JsonPropertyName("onlineRepositories")]
    public List<ExtensionRepositoryDto> Repositories { get; set; } = [];
}
