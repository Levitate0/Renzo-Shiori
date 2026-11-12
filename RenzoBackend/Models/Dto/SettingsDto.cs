using System.Text.Json.Serialization;
using RenzoBackend.Extensions;

namespace RenzoBackend.Models.Dto;

public class SettingsDto : EditableSettingsDto
{
    private string _storageFolder = string.Empty;
    private string _importFolder = string.Empty;

    [JsonPropertyName("storageFolder")]
    public string StorageFolder
    {
        get => _storageFolder.SanitizeDirectory();
        set => _storageFolder = value;
    }

    [JsonPropertyName("importFolder")]
    public string ImportFolder
    {
        get => _importFolder.SanitizeDirectory();
        set => _importFolder = value;
    }

    // Stored secrets never leave the server. GET /api/settings is readable by
    // every signed-in user (they need the categories, languages and the rest),
    // and with sign-in off by anyone, so it used to hand out the SMTP and SOCKS
    // passwords in plain text. Clients get a blank field plus these flags, and a
    // blank field sent back means "keep what is stored" (KeepStoredSecrets).

    /// <summary>True when an SMTP password is stored. Output only.</summary>
    [JsonPropertyName("smtpPasswordSet")]
    public bool SmtpPasswordSet { get; set; }

    /// <summary>True when a SOCKS proxy password is stored. Output only.</summary>
    [JsonPropertyName("socksProxyPasswordSet")]
    public bool SocksProxyPasswordSet { get; set; }

    /// <summary>
    /// A copy safe to send to a client: the passwords blanked and only their
    /// presence reported. A shallow copy — the cached instance is never touched.
    /// </summary>
    public SettingsDto WithoutSecrets()
    {
        var copy = (SettingsDto)MemberwiseClone();
        copy.SmtpPasswordSet = !string.IsNullOrEmpty(SmtpPassword);
        copy.SmtpPassword = string.Empty;
        copy.SocksProxyPasswordSet = !string.IsNullOrEmpty(SocksProxyPassword);
        copy.SocksProxyPassword = string.Empty;
        return copy;
    }

    /// <summary>
    /// For a save from a client, which only ever saw blanks: a blank password
    /// keeps the <paramref name="stored"/> one, so saving any other setting no
    /// longer wipes it. Typing a new one replaces it. Clearing the username
    /// clears its password too — that is how a password is removed, and neither
    /// password is used without a username anyway.
    /// </summary>
    public void KeepStoredSecrets(EditableSettingsDto stored)
    {
        SmtpPassword = Kept(SmtpPassword, SmtpUsername, stored.SmtpPassword);
        SocksProxyPassword = Kept(SocksProxyPassword, SocksProxyUsername, stored.SocksProxyPassword);
    }

    private static string Kept(string? sent, string? username, string? stored) =>
        !string.IsNullOrEmpty(sent) ? sent
        : string.IsNullOrWhiteSpace(username) ? string.Empty
        : stored ?? string.Empty;
}