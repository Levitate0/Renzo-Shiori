using System.Text.Json.Serialization;

namespace Mihon.ExtensionsBridge.Models;

/// <summary>
/// The "index_v2" repository index, as advertised by repo.json.
///
/// Keiyoushi (and Mihon 0.20.1+) replaced the flat v1 array with this shape. The
/// canonical form is protobuf (index.pb); the identical data is also published as
/// JSON (index.json), which is what we read.
///
/// The v1 index files have NOT gone away, but they are no longer the real thing:
/// index.min.json now serves two placeholder entries ("Outdated App", "Update to
/// Mihon 0.20.1+") whose only job is to tell stale clients to upgrade. A client
/// that keeps reading it parses successfully and quietly ends up with a catalogue
/// of two — which is exactly what a broken repo update looks like from the UI.
/// </summary>
public record RepositoryIndexV2
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("signingKey")]
    public string? SigningKey { get; set; }

    [JsonPropertyName("contact")]
    public RepositoryIndexV2Contact? Contact { get; set; }

    [JsonPropertyName("extensionList")]
    public RepositoryIndexV2List? ExtensionList { get; set; }
}

public record RepositoryIndexV2Contact
{
    [JsonPropertyName("website")]
    public string? Website { get; set; }

    [JsonPropertyName("discord")]
    public string? Discord { get; set; }
}

public record RepositoryIndexV2List
{
    [JsonPropertyName("extensions")]
    public List<RepositoryIndexV2Extension> Extensions { get; set; } = [];
}

public record RepositoryIndexV2Extension
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("packageName")]
    public string? PackageName { get; set; }

    [JsonPropertyName("resources")]
    public RepositoryIndexV2Resources? Resources { get; set; }

    /// <summary>Extension-library major.minor, e.g. "1.6" — the v1 "code" field.</summary>
    [JsonPropertyName("extensionLib")]
    public string? ExtensionLib { get; set; }

    /// <summary>A string in v2 where v1 used a number, so it is parsed leniently.</summary>
    [JsonPropertyName("versionCode")]
    public string? VersionCode { get; set; }

    [JsonPropertyName("versionName")]
    public string? VersionName { get; set; }

    /// <summary>"CONTENT_WARNING_NSFW" replaces v1's nsfw: 0|1.</summary>
    [JsonPropertyName("contentWarning")]
    public string? ContentWarning { get; set; }

    [JsonPropertyName("sources")]
    public List<RepositoryIndexV2Source> Sources { get; set; } = [];
}

public record RepositoryIndexV2Resources
{
    /// <summary>Absolute, and often on a CDN rather than the repo host.</summary>
    [JsonPropertyName("apkUrl")]
    public string? ApkUrl { get; set; }

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("jarUrl")]
    public string? JarUrl { get; set; }
}

public record RepositoryIndexV2Source
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("language")]
    public string? Language { get; set; }

    [JsonPropertyName("homeUrl")]
    public string? HomeUrl { get; set; }
}
