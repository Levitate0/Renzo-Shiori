using System.IO;
using System.Text.Json;

namespace RensaioWindows;

/// <summary>
/// Persists the configured server address under %AppData%\Rensaio\settings.json.
/// </summary>
public class ServerConfig
{
    public string? ServerUrl { get; set; }

    private static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rensaio");

    private static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    public static ServerConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                return JsonSerializer.Deserialize<ServerConfig>(File.ReadAllText(ConfigPath)) ?? new ServerConfig();
        }
        catch
        {
            // Corrupt config: fall through to defaults, user just re-enters the address.
        }
        return new ServerConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this));
    }
}
