using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RenzoWindows;

/// <summary>
/// Shared offline storage for the desktop client — file writes, the manifest
/// (JSON, matching the web app's OfflineManifest v2), a small KV store, the
/// chosen download folder, and the download job queue. Mirrors the Android
/// <c>RenzoStore</c> so the shared web/offline code behaves identically on both
/// platforms. Thread-safe: the UI-thread bridge and the background downloader
/// both touch it.
/// </summary>
public sealed class RenzoStore
{
    private static readonly string BaseDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RenzoShiori");

    private readonly string _kvPath = Path.Combine(BaseDir, "kv.json");
    private readonly string _settingsPath = Path.Combine(BaseDir, "offline-settings.json");
    private readonly object _lock = new();

    private const string ManifestKey = "renzo.offline.manifest.v1";

    public RenzoStore()
    {
        Directory.CreateDirectory(BaseDir);
    }

    // ── download location ─────────────────────────────────────────────────────
    private sealed class Settings
    {
        public string? DownloadFolder { get; set; }
        public List<string> Jobs { get; set; } = new();
    }

    private Settings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath)) ?? new Settings();
        }
        catch { /* corrupt — start fresh */ }
        return new Settings();
    }

    private void SaveSettings(Settings s) => File.WriteAllText(_settingsPath, JsonSerializer.Serialize(s));

    /// <summary>Root the offline files live under: the chosen folder, else an app-private default.</summary>
    private string DownloadRoot()
    {
        string chosen = LoadSettings().DownloadFolder ?? Path.Combine(BaseDir, "Downloads");
        return Path.Combine(chosen, "RenzoShiori");
    }

    /// <summary>Absolute path for an app-relative offline path (kept inside the download root).</summary>
    private string Resolve(string relPath)
    {
        string[] segs = relPath.Replace('\\', '/').Split('/')
            .Where(p => p.Length > 0 && p != "." && p != "..").ToArray();
        return Path.Combine(new[] { DownloadRoot() }.Concat(segs).ToArray());
    }

    public string? FolderLabel() => LoadSettings().DownloadFolder;

    public void SetFolder(string? path)
    {
        lock (_lock)
        {
            Settings s = LoadSettings();
            s.DownloadFolder = string.IsNullOrWhiteSpace(path) ? null : path;
            SaveSettings(s);
        }
    }

    // ── files ──────────────────────────────────────────────────────────────────
    public void WriteFile(string relPath, byte[] bytes)
    {
        string full = Resolve(relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    public byte[]? ReadFile(string relPath)
    {
        string full = Resolve(relPath);
        return File.Exists(full) ? File.ReadAllBytes(full) : null;
    }

    public bool Exists(string relPath) => File.Exists(Resolve(relPath)) || Directory.Exists(Resolve(relPath));

    public void DeletePath(string relPath)
    {
        string full = Resolve(relPath);
        try
        {
            if (Directory.Exists(full)) Directory.Delete(full, true);
            else if (File.Exists(full)) File.Delete(full);
        }
        catch { /* best-effort */ }
    }

    // ── KV store ────────────────────────────────────────────────────────────────
    private Dictionary<string, string> LoadKv()
    {
        try
        {
            if (File.Exists(_kvPath))
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_kvPath)) ?? new();
        }
        catch { /* corrupt — start fresh */ }
        return new();
    }

    private void SaveKv(Dictionary<string, string> kv) => File.WriteAllText(_kvPath, JsonSerializer.Serialize(kv));

    public string? KvGet(string key)
    {
        lock (_lock)
        {
            return LoadKv().TryGetValue(key, out string? v) ? v : null;
        }
    }

    public void KvSet(string key, string value)
    {
        lock (_lock)
        {
            Dictionary<string, string> kv = LoadKv();
            kv[key] = value;
            SaveKv(kv);
        }
    }

    // ── manifest (v2, matches the web app's OfflineManifest) ─────────────────────
    private static JsonObject EmptyManifest() => new()
    {
        ["version"] = 2,
        ["series"] = new JsonObject(),
        ["chapters"] = new JsonObject(),
    };

    /// <summary>Read the manifest under the lock (caller mutates), always well-formed.</summary>
    public JsonObject GetManifest()
    {
        string? raw = KvGet(ManifestKey);
        if (string.IsNullOrEmpty(raw)) return EmptyManifest();
        try
        {
            JsonObject m = JsonNode.Parse(raw)?.AsObject() ?? EmptyManifest();
            m["version"] = 2;
            m["series"] ??= new JsonObject();
            m["chapters"] ??= new JsonObject();
            return m;
        }
        catch
        {
            return EmptyManifest();
        }
    }

    public void SetManifest(JsonObject m) => KvSet(ManifestKey, m.ToJsonString());

    /// <summary>Read-modify-write the manifest atomically w.r.t. other store callers.</summary>
    public void UpdateManifest(Action<JsonObject> mutate)
    {
        lock (_lock)
        {
            JsonObject m = GetManifest();
            mutate(m);
            SetManifest(m);
        }
    }

    public bool HasChapter(string chapterKey) =>
        GetManifest()["chapters"]?.AsObject().ContainsKey(chapterKey) == true;

    public bool HasDownloads() =>
        (GetManifest()["chapters"]?.AsObject().Count ?? 0) > 0;

    // ── download job queue ───────────────────────────────────────────────────────
    // Persisted (not passed inline) so the downloader can drain across restarts and
    // a big batch can't be lost — mirrors the Android store's queue.
    public void EnqueueJob(string payloadJson)
    {
        lock (_lock)
        {
            Settings s = LoadSettings();
            s.Jobs.Add(payloadJson);
            SaveSettings(s);
        }
    }

    public string? TakeJob()
    {
        lock (_lock)
        {
            Settings s = LoadSettings();
            if (s.Jobs.Count == 0) return null;
            string first = s.Jobs[0];
            s.Jobs.RemoveAt(0);
            SaveSettings(s);
            return first;
        }
    }

    public void ClearJobs()
    {
        lock (_lock)
        {
            Settings s = LoadSettings();
            s.Jobs.Clear();
            SaveSettings(s);
        }
    }
}
