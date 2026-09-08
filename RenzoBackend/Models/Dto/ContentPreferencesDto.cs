using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

/// <summary>
/// One user's Content Preferences.
///
/// A null field means "inherit the server default" on the way IN, and is never
/// null on the way out — a GET always returns the values actually in force, so
/// a client never has to merge against the global settings itself.
/// </summary>
public class ContentPreferencesDto
{
    /// <summary>Language codes, most preferred first. Null clears the override.</summary>
    [JsonPropertyName("preferredLanguages")]
    public string[]? PreferredLanguages { get; set; }

    [JsonPropertyName("nsfwVisibility")]
    public NsfwVisibility? NsfwVisibility { get; set; }

    [JsonPropertyName("downloadAllChapters")]
    public bool? DownloadAllChapters { get; set; }
}
