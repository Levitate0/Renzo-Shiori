using RenzoBackend.Data;
using RenzoBackend.Services.Providers;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RenzoBackend.Services.SiteAuth;

/// <summary>
/// A discovered coin/paid site: its display name, the web + API hosts pulled
/// from the extension itself, the login endpoint, and the form fields. Fields
/// marked <see cref="Confirmed"/> came from a login that actually worked and are
/// persisted locally; everything else is auto-discovered / best-effort.
/// </summary>
public class CoinSiteDefinition
{
    public string Provider { get; set; } = "";
    public string Domain { get; set; } = "";           // web host, e.g. ezmanga.org
    public string? ApiBase { get; set; }               // API host+base, e.g. https://vapi.ezmanga.org/api/v1
    public string LoginUrl { get; set; } = "";
    public string UsernameField { get; set; } = "email";
    public string PasswordField { get; set; } = "password";
    public string? CsrfPageUrl { get; set; }
    public string? CsrfField { get; set; }
    // Some login forms gate on the clicked <input type="submit" name="..." value="...">
    // being present in the POST body (e.g. to tell which of several forms on the
    // page was submitted) — a real browser includes it automatically, so a plain
    // username+password POST that omits it can get silently ignored server-side
    // while still returning 200 with a baseline session cookie, looking like
    // success. Optional: most sites don't need this.
    public string? SubmitField { get; set; }
    public string? SubmitValue { get; set; }
    public string SessionCookieName { get; set; } = "";
    public bool Confirmed { get; set; }
}

/// <summary>
/// Builds the coin-site login list AT RUNTIME instead of from a hardcoded table:
///
///  1. A source is a coin site if its own preferences mention coins/locked/
///     premium/paid chapters (the extension advertises this itself).
///  2. Its web + API hosts are extracted from the extension APK on disk (the
///     URLs the extension talks to), with the series URL in the DB as a fallback.
///  3. A confirmed login definition (endpoint + field names that actually
///     worked) is persisted to a LOCAL file under the config dir —
///     site-logins.local.json — never committed to the repo. So new installable
///     sources light up automatically, and per-instance login details stay local.
/// </summary>
public class CoinSiteRegistry
{
    private readonly AppDbContext _db;
    private readonly ProviderCacheService _providerCache;
    private readonly ILogger _logger;
    private readonly string _localFile;
    private readonly string _extensionsDir;

    // A coin/paid source describes gated chapters with these words. Matched on
    // WORD BOUNDARIES — naive substring matching mis-fired ("bLOCKED uploader",
    // "genre BLOCK" contain "locked"/"block"). "coin(s)" also requires a
    // chapter/read context so a stray "coin" in flavor text can't trip it.
    private static readonly Regex CoinPattern =
        new(@"\b(locked|premium|paid|unlock|purchase|subscription|coins?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Preference phrases that are filters/blocklists, not coin gates — skipped.
    private static readonly Regex FilterPattern =
        new(@"\b(block|blocked|genre|genres|group|groups|uploader|uploaders|uuid|tag|tags|scanlator)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Field-name hints by login shape, applied when we can only discover the host.
    private static readonly string[] UserFieldGuesses = { "email", "username", "login", "log" };

    private readonly object _lock = new();
    private Dictionary<string, CoinSiteDefinition> _local = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, (string? web, string? api)> _extractCache = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<long, string>? _coinScan;
    private static Dictionary<long, string>? _allScan;
    private bool _loaded;

    public CoinSiteRegistry(AppDbContext db, ProviderCacheService providerCache,
        IConfiguration config, ILogger<CoinSiteRegistry> logger)
    {
        _db = db;
        _providerCache = providerCache;
        _logger = logger;
        string runtimeDir = config["runtimeDirectory"] ?? ".";
        _localFile = Path.Combine(runtimeDir, "site-logins.local.json");
        _extensionsDir = Path.Combine(runtimeDir, config.GetValue("BridgeFolder", "mihon"), "extensions");
    }

    private void EnsureLoaded()
    {
        lock (_lock)
        {
            if (_loaded)
                return;
            _loaded = true;
            try
            {
                if (File.Exists(_localFile))
                {
                    var list = JsonSerializer.Deserialize<List<CoinSiteDefinition>>(File.ReadAllText(_localFile));
                    if (list != null)
                        _local = list.ToDictionary(d => d.Provider, d => d, StringComparer.OrdinalIgnoreCase);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read local site-logins file");
            }
        }
    }

    /// <summary>Persists a confirmed (or updated) login definition to the local file.</summary>
    public void SaveLocal(CoinSiteDefinition def)
    {
        EnsureLoaded();
        lock (_lock)
        {
            _local[def.Provider] = def;
            try
            {
                File.WriteAllText(_localFile,
                    JsonSerializer.Serialize(_local.Values.ToList(), new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not write local site-logins file");
            }
        }
    }

    /// <summary>
    /// Every installed source that gates chapters behind coins, each with the
    /// best login definition we can assemble (local-confirmed > extracted >
    /// heuristic). Detection reads each source's own preferences, so a newly
    /// installed coin source appears automatically.
    /// </summary>
    public async Task<List<CoinSiteDefinition>> GetCoinSitesAsync(CancellationToken token = default)
    {
        EnsureLoaded();

        // Coin-detection is read from each extension's on-disk preferences.json
        // (keyed by SourceId) — NOT by loading every extension through the JVM
        // bridge, which took ~30s for the whole install and hung the UI.
        Dictionary<long, string> coinSourceDirs = ScanCoinSourceDirs();

        var result = new Dictionary<string, CoinSiteDefinition>(StringComparer.OrdinalIgnoreCase);
        List<Models.Database.ProviderStorageEntity> providers =
            await _providerCache.GetCachedProvidersAsync(token).ConfigureAwait(false);

        foreach (var p in providers)
        {
            if (result.ContainsKey(p.Name))
                continue;
            if (!long.TryParse(p.SourceSourceId, out long sid) || !coinSourceDirs.TryGetValue(sid, out string? extDir))
                continue;

            CoinSiteDefinition def = await BuildDefinitionAsync(p.Name, extDir, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(def.Domain))
                result[p.Name] = def;
        }

        return result.Values.OrderBy(d => d.Provider).ToList();
    }

    /// <summary>
    /// Scans every extension's on-disk preferences.json for coin/paid-chapter
    /// language and returns SourceId -> extension directory for the matches.
    /// Cheap (filesystem + regex) and cached for the process lifetime keyed by
    /// the extensions dir's last-write time.
    /// </summary>
    private Dictionary<long, string> ScanCoinSourceDirs()
    {
        lock (_lock)
        {
            if (_coinScan != null)
                return _coinScan;
        }

        var map = new Dictionary<long, string>();
        try
        {
            if (Directory.Exists(_extensionsDir))
            {
                foreach (string prefFile in Directory.EnumerateFiles(_extensionsDir, "preferences.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(prefFile));
                        foreach (JsonElement src in doc.RootElement.EnumerateArray())
                        {
                            if (!src.TryGetProperty("SourceId", out JsonElement idEl))
                                continue;
                            long sid = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64()
                                : long.TryParse(idEl.GetString(), out long s) ? s : 0;
                            if (sid == 0 || !src.TryGetProperty("Preferences", out JsonElement prefs))
                                continue;
                            if (PrefsLookCoinGated(prefs))
                                map[sid] = Path.GetDirectoryName(prefFile)!;
                        }
                    }
                    catch { /* skip an unreadable prefs file */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Coin-source scan failed");
        }

        lock (_lock) { _coinScan = map; }
        return map;
    }

    /// <summary>
    /// SourceId -> extension directory for EVERY installed source (no coin
    /// filter). Backs login support for sources that gate chapters without
    /// advertising it in their preferences (Keyoapp/Madara/WordPress themes).
    /// </summary>
    private Dictionary<long, string> ScanAllSourceDirs()
    {
        lock (_lock)
        {
            if (_allScan != null)
                return _allScan;
        }

        var map = new Dictionary<long, string>();
        try
        {
            if (Directory.Exists(_extensionsDir))
            {
                foreach (string prefFile in Directory.EnumerateFiles(_extensionsDir, "preferences.json", SearchOption.AllDirectories))
                {
                    try
                    {
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(prefFile));
                        foreach (JsonElement src in doc.RootElement.EnumerateArray())
                        {
                            if (!src.TryGetProperty("SourceId", out JsonElement idEl))
                                continue;
                            long sid = idEl.ValueKind == JsonValueKind.Number ? idEl.GetInt64()
                                : long.TryParse(idEl.GetString(), out long s) ? s : 0;
                            if (sid == 0)
                                continue;
                            map[sid] = Path.GetDirectoryName(prefFile)!;
                        }
                    }
                    catch { /* skip an unreadable prefs file */ }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Source dir scan failed");
        }

        lock (_lock) { _allScan = map; }
        return map;
    }

    private static bool PrefsLookCoinGated(JsonElement prefs)
    {
        foreach (JsonElement pref in prefs.EnumerateArray())
        {
            string title = pref.TryGetProperty("Title", out var t) ? (t.GetString() ?? "") : "";
            string summary = pref.TryGetProperty("Summary", out var s) ? (s.GetString() ?? "") : "";
            string blob = title + " " + summary;
            if (FilterPattern.IsMatch(blob))
                continue;
            if (CoinPattern.IsMatch(blob))
                return true;
        }
        return false;
    }

    public async Task<CoinSiteDefinition?> GetDefinitionAsync(string provider, CancellationToken token = default)
    {
        EnsureLoaded();
        lock (_lock)
        {
            if (_local.TryGetValue(provider, out var confirmed) && confirmed.Confirmed)
                return confirmed;
        }
        var all = await GetCoinSitesAsync(token).ConfigureAwait(false);
        var coin = all.FirstOrDefault(d => d.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (coin != null)
            return coin;
        // Not auto-detected as a coin site — still resolve a definition for any
        // installed source so its login/cookie-paste works (many sites gate
        // chapters without saying so in their extension preferences).
        return await BuildForAnyProviderAsync(provider, token).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a best-effort login definition for any installed source by name,
    /// resolving its host from the extension APK or, failing that, a stored series
    /// URL. Returns null when no domain can be determined.
    /// </summary>
    private async Task<CoinSiteDefinition?> BuildForAnyProviderAsync(string provider, CancellationToken token)
    {
        string extDir = "";
        var providers = await _providerCache.GetCachedProvidersAsync(token).ConfigureAwait(false);
        var p = providers.FirstOrDefault(x => x.Name.Equals(provider, StringComparison.OrdinalIgnoreCase));
        if (p != null && long.TryParse(p.SourceSourceId, out long sid))
            ScanAllSourceDirs().TryGetValue(sid, out extDir!);
        CoinSiteDefinition def = await BuildDefinitionAsync(provider, extDir ?? "", token).ConfigureAwait(false);
        return string.IsNullOrEmpty(def.Domain) ? null : def;
    }

    /// <summary>
    /// Sites the user can add a login for: every auto-detected coin site, plus any
    /// source they actually have series from (so gated chapters on Keyoapp/Madara/
    /// WordPress-style sites like Violet Scans can be unlocked even though those
    /// don't advertise a coin gate). Each entry is flagged as coin-detected or not.
    /// </summary>
    public async Task<List<(CoinSiteDefinition def, bool coin)>> GetLoginableSitesAsync(CancellationToken token = default)
    {
        var byName = new Dictionary<string, (CoinSiteDefinition def, bool coin)>(StringComparer.OrdinalIgnoreCase);

        foreach (var d in await GetCoinSitesAsync(token).ConfigureAwait(false))
            byName[d.Provider] = (d, true);

        List<string> providersWithSeries = await _db.SeriesProviders
            .Where(a => a.Url != null && a.Url != "" && !a.IsLocal && !a.IsUnknown)
            .Select(a => a.Provider)
            .Distinct()
            .ToListAsync(token).ConfigureAwait(false);

        foreach (string name in providersWithSeries)
        {
            if (byName.ContainsKey(name))
                continue;
            CoinSiteDefinition? def = await BuildForAnyProviderAsync(name, token).ConfigureAwait(false);
            if (def != null)
                byName[name] = (def, false);
        }

        return byName.Values
            .OrderByDescending(x => x.coin)
            .ThenBy(x => x.def.Provider, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<CoinSiteDefinition> BuildDefinitionAsync(string provider, string extDir, CancellationToken token)
    {
        // Local confirmed definition wins outright.
        lock (_lock)
        {
            if (_local.TryGetValue(provider, out var local) && local.Confirmed)
                return local;
        }

        (string? web, string? api) = ExtractHosts(extDir);
        // Fall back to the domain of a series URL for this source.
        if (string.IsNullOrEmpty(web))
            web = await DomainFromSeriesUrlAsync(provider, token).ConfigureAwait(false);

        string domain = (web ?? "").Replace("https://", "").Replace("http://", "").TrimEnd('/');
        // Prefer the API host for the login POST (that's where these SPAs auth);
        // otherwise the web host.
        string loginBase = api ?? (string.IsNullOrEmpty(domain) ? "" : "https://" + domain);

        var def = new CoinSiteDefinition
        {
            Provider = provider,
            Domain = domain,
            ApiBase = api,
            LoginUrl = string.IsNullOrEmpty(loginBase) ? "" : loginBase.TrimEnd('/') + "/auth/login",
            UsernameField = "email",
            PasswordField = "password",
            CsrfPageUrl = string.IsNullOrEmpty(domain) ? null : "https://" + domain + "/",
            CsrfField = "_token",
            SessionCookieName = "",   // unknown until a login succeeds; empty = accept any new cookie
            Confirmed = false,
        };

        // Merge any locally-saved (unconfirmed) tweaks the user/system stored.
        lock (_lock)
        {
            if (_local.TryGetValue(provider, out var saved))
            {
                def.LoginUrl = string.IsNullOrEmpty(saved.LoginUrl) ? def.LoginUrl : saved.LoginUrl;
                def.UsernameField = saved.UsernameField;
                def.PasswordField = saved.PasswordField;
                def.SessionCookieName = saved.SessionCookieName;
                def.SubmitField = saved.SubmitField;
                def.SubmitValue = saved.SubmitValue;
                if (!string.IsNullOrEmpty(saved.Domain)) def.Domain = saved.Domain;
                if (saved.ApiBase != null) def.ApiBase = saved.ApiBase;
            }
        }
        return def;
    }

    /// <summary>Login/candidate URLs to try, in order, for a discovered site.</summary>
    public IEnumerable<string> CandidateLoginUrls(CoinSiteDefinition def)
    {
        var bases = new List<string>();
        if (def.ApiBase != null) bases.Add(def.ApiBase.TrimEnd('/'));
        if (!string.IsNullOrEmpty(def.Domain)) bases.Add("https://" + def.Domain);
        var seen = new HashSet<string>();
        // The confirmed/stored URL first.
        if (!string.IsNullOrEmpty(def.LoginUrl) && seen.Add(def.LoginUrl))
            yield return def.LoginUrl;
        foreach (string b in bases)
            foreach (string path in new[]
            {
                "/auth/login", "/login", "/api/auth/login", "/api/login", "/user/login", "/sign-in",
                // better-auth (the "vcomics"/Astro coin platform, e.g. Magus Manga)
                // — its sign-in endpoint doesn't match any of the classic shapes,
                // so without these a new site on that platform can't be discovered.
                "/api/auth/sign-in/email", "/auth/sign-in/email", "/sign-in/email",
            })
                if (seen.Add(b + path))
                    yield return b + path;
    }

    /// <summary>
    /// Pulls the web host and API base URL from the extension APK by scanning
    /// its dex for https URLs. Cached per package (the APK doesn't change).
    /// </summary>
    private (string? web, string? api) ExtractHosts(string extDir)
    {
        lock (_lock)
        {
            if (_extractCache.TryGetValue(extDir, out var cached))
                return cached;
        }

        string? web = null, api = null;
        try
        {
            string? apk = Directory.Exists(extDir)
                ? Directory.EnumerateFiles(extDir, "*.apk", SearchOption.AllDirectories).FirstOrDefault()
                : null;
            if (apk != null)
            {
                var urls = new List<string>();
                using ZipArchive zip = ZipFile.OpenRead(apk);
                foreach (ZipArchiveEntry e in zip.Entries.Where(e => e.Name.EndsWith(".dex")))
                {
                    using Stream s = e.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    string text = System.Text.Encoding.Latin1.GetString(ms.ToArray());
                    urls.AddRange(Regex.Matches(text, @"https://[a-zA-Z0-9.]+(?:/[a-zA-Z0-9./_-]*)?")
                        .Select(m => m.Value));
                }
                // The API base is the shortest URL on an api./vapi.* host or one
                // that contains /api/. The web host is the most common bare host.
                api = urls.Where(u => Regex.IsMatch(u, @"https://(?:v?api|api-)\.", RegexOptions.IgnoreCase) || u.Contains("/api"))
                    .OrderBy(u => u.Length)
                    .FirstOrDefault();
                web = urls.Select(u => Regex.Match(u, @"https://[a-zA-Z0-9.]+").Value)
                    .Where(h => !h.Contains("api.") && !h.Contains("vapi.") &&
                                !h.Contains("github") && !h.Contains("gstatic") && !h.Contains("googleapis"))
                    .GroupBy(h => h).OrderByDescending(g => g.Count())
                    .Select(g => g.Key).FirstOrDefault();
                if (api != null)
                {
                    // Trim a trailing endpoint fragment to the base (…/api/v1).
                    Match b = Regex.Match(api, @"^(https://[a-zA-Z0-9.]+(?:/api(?:/v\d+)?)?)", RegexOptions.IgnoreCase);
                    if (b.Success) api = b.Groups[1].Value;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Host extraction failed for {Dir}", extDir);
        }

        var pair = (web, api);
        lock (_lock) { _extractCache[extDir] = pair; }
        return pair;
    }

    private async Task<string?> DomainFromSeriesUrlAsync(string provider, CancellationToken token)
    {
        string? url = await _db.LatestSeries.Where(a => a.Provider == provider && a.Url != null)
            .Select(a => a.Url).FirstOrDefaultAsync(token).ConfigureAwait(false);
        url ??= await _db.SeriesProviders.Where(a => a.Provider == provider && a.Url != null)
            .Select(a => a.Url).FirstOrDefaultAsync(token).ConfigureAwait(false);
        if (url == null)
            return null;
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? "https://" + u.Host : null;
    }

    /// <summary>The username-field guesses, for the login engine to try in turn.</summary>
    public static IReadOnlyList<string> UsernameFieldGuesses => UserFieldGuesses;
}
