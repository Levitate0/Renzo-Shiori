using System.Text.Json;

namespace RenzoBackend.Services.Providers;

/// <summary>
/// Where each installed source lives NOW, and where it used to.
///
/// Scanlation sites move domain all the time (violetscans.org → violetmanga.com,
/// reimanga.com → reimanga.net, kagane.to → kagane.si, …) and the extension
/// follows with a new <c>baseUrl</c>. Everything Renzo Shiori stored before the
/// move still names the old host: series and chapter links, covers, the
/// site-login definition. It keeps working only while the old domain redirects,
/// and a login POST does not survive the redirect at all.
///
/// The evidence is the extension's own version history in
/// <c>local_repository.json</c> (every installed version keeps its source list
/// with the base URL it shipped), plus a local record of every base URL seen,
/// kept so a move is still recognised after old versions are pruned. Only the
/// extension's word counts: a host is "previous" because a version of the SAME
/// source (same id) used it as its base URL.
/// </summary>
public class SourceHostsService
{
    private readonly ILogger<SourceHostsService> _logger;
    private readonly string _repositoryFile;
    private readonly string _knownFile;

    private readonly object _lock = new();
    private DateTime _loadedStamp;
    private Dictionary<long, Uri> _current = new();
    private Dictionary<long, HashSet<string>> _seen = new();

    public SourceHostsService(IConfiguration config, ILogger<SourceHostsService> logger)
    {
        _logger = logger;
        string runtimeDir = config["runtimeDirectory"] ?? ".";
        string extensions = Path.Combine(runtimeDir, config.GetValue("BridgeFolder", "mihon"), "extensions");
        _repositoryFile = Path.Combine(extensions, "local_repository.json");
        _knownFile = Path.Combine(runtimeDir, "source-hosts.local.json");
    }

    /// <summary>The installed (active) version's base URL for a source, or null when it isn't installed.</summary>
    public Uri? CurrentBaseUrl(long sourceId)
    {
        Refresh();
        lock (_lock)
            return _current.TryGetValue(sourceId, out Uri? u) ? u : null;
    }

    /// <summary>The installed version's base URL for a source id written as text (as the DB stores it).</summary>
    public Uri? CurrentBaseUrl(string? sourceId) =>
        long.TryParse(sourceId, out long id) ? CurrentBaseUrl(id) : null;

    /// <summary>
    /// Hosts this source used as its base URL before the current one, normalised
    /// (lower case, no "www."). Empty when it never moved.
    /// </summary>
    public IReadOnlyCollection<string> PreviousHosts(long sourceId)
    {
        Refresh();
        lock (_lock)
        {
            if (!_seen.TryGetValue(sourceId, out HashSet<string>? all))
                return Array.Empty<string>();
            string? now = _current.TryGetValue(sourceId, out Uri? u) ? Normalize(u.Host) : null;
            return all.Where(h => h != now).ToList();
        }
    }

    /// <summary>Every installed source id with its current base URL.</summary>
    public IReadOnlyDictionary<long, Uri> AllCurrent()
    {
        Refresh();
        lock (_lock)
            return new Dictionary<long, Uri>(_current);
    }

    /// <summary>Lower case, without a leading "www.": the form hosts are compared in.</summary>
    public static string Normalize(string host)
    {
        string h = host.Trim().TrimEnd('.').ToLowerInvariant();
        return h.StartsWith("www.", StringComparison.Ordinal) ? h[4..] : h;
    }

    private void Refresh()
    {
        DateTime stamp;
        try
        {
            stamp = File.Exists(_repositoryFile) ? File.GetLastWriteTimeUtc(_repositoryFile) : DateTime.MinValue;
        }
        catch
        {
            return;
        }
        lock (_lock)
        {
            if (stamp == _loadedStamp && _current.Count > 0)
                return;
        }

        var current = new Dictionary<long, Uri>();
        var seen = new Dictionary<long, HashSet<string>>();
        try
        {
            if (File.Exists(_repositoryFile))
            {
                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(_repositoryFile));
                foreach (JsonElement group in doc.RootElement.EnumerateArray())
                {
                    int active = group.TryGetProperty("ActiveEntry", out JsonElement a) && a.ValueKind == JsonValueKind.Number ? a.GetInt32() : 0;
                    if (!group.TryGetProperty("Entries", out JsonElement entries) || entries.ValueKind != JsonValueKind.Array)
                        continue;
                    int i = 0;
                    foreach (JsonElement entry in entries.EnumerateArray())
                    {
                        bool isActive = i++ == active;
                        if (!entry.TryGetProperty("Extension", out JsonElement ext) ||
                            !ext.TryGetProperty("sources", out JsonElement sources) || sources.ValueKind != JsonValueKind.Array)
                            continue;
                        foreach (JsonElement src in sources.EnumerateArray())
                        {
                            string? idText = src.TryGetProperty("id", out JsonElement idEl)
                                ? (idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : idEl.GetString()) : null;
                            string? baseUrl = src.TryGetProperty("BaseUrl", out JsonElement b) ? b.GetString() : null;
                            if (!long.TryParse(idText, out long id) || string.IsNullOrWhiteSpace(baseUrl) ||
                                !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out Uri? uri) || string.IsNullOrEmpty(uri.Host))
                                continue;
                            if (!seen.TryGetValue(id, out HashSet<string>? hosts))
                                seen[id] = hosts = new HashSet<string>();
                            hosts.Add(Normalize(uri.Host));
                            if (isActive)
                                current[id] = uri;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Mid-write by an extension install: keep what was read last time.
            _logger.LogDebug(ex, "Could not read {File}", _repositoryFile);
            return;
        }

        // Fold in (and extend) the local record, so a pruned old version still
        // counts as evidence of where a source used to be.
        Dictionary<long, HashSet<string>> known = ReadKnown();
        bool grew = false;
        foreach ((long id, HashSet<string> hosts) in seen)
        {
            if (!known.TryGetValue(id, out HashSet<string>? k))
                known[id] = k = new HashSet<string>();
            foreach (string h in hosts)
                grew |= k.Add(h);
        }
        foreach ((long id, HashSet<string> k) in known)
        {
            if (!seen.TryGetValue(id, out HashSet<string>? s))
                seen[id] = s = new HashSet<string>();
            s.UnionWith(k);
        }
        if (grew)
            WriteKnown(known);

        lock (_lock)
        {
            _current = current;
            _seen = seen;
            _loadedStamp = stamp;
        }
    }

    private Dictionary<long, HashSet<string>> ReadKnown()
    {
        try
        {
            if (!File.Exists(_knownFile))
                return new();
            var raw = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(_knownFile)) ?? new();
            return raw.Where(kv => long.TryParse(kv.Key, out _))
                .ToDictionary(kv => long.Parse(kv.Key), kv => kv.Value.Select(Normalize).ToHashSet());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read {File}", _knownFile);
            return new();
        }
    }

    private void WriteKnown(Dictionary<long, HashSet<string>> known)
    {
        try
        {
            string json = JsonSerializer.Serialize(
                known.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.OrderBy(h => h).ToList()),
                new JsonSerializerOptions { WriteIndented = true });
            string tmp = _knownFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _knownFile, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not write {File}", _knownFile);
        }
    }
}
