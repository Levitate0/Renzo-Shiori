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

}