using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Mihon.ExtensionsBridge.Models;

public record TachiyomiExtension
{
    [JsonPropertyName("name")]
    public string Name { get; set; }
    [JsonPropertyName("pkg")]
    public string Package { get; set; }
    [JsonPropertyName("apk")]
    public string Apk { get; set; }
    [JsonPropertyName("lang")]
    public string Language { get; set; }
    [JsonPropertyName("code")]
    public int VersionCode { get; set; }
    [JsonPropertyName("version")]
    public string Version { get; set; }
    [JsonPropertyName("nsfw")]
    public int Nsfw { get; set; }
    [JsonPropertyName("sources")]
    public List<TachiyomiSource> Sources { get; set; } = [];

    /// <summary>
    /// Absolute APK URL, when the index supplies one (v2 does; v1 did not).
    /// v2 repos serve artifacts from a CDN that is NOT the repo host, so the
    /// old "{repo}/apk/{filename}" convention does not hold and this must win
    /// where present. Null for v1 repos, which keep the composed URL.
    /// </summary>
    [JsonPropertyName("apkUrl")]
    public string? ApkUrl { get; set; }

    /// <summary>Absolute icon URL from a v2 index; null for v1.</summary>
    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }
}
