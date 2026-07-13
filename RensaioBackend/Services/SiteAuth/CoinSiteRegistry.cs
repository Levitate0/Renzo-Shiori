using RensaioBackend.Data;
using RensaioBackend.Services.Providers;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RensaioBackend.Services.SiteAuth;

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
    private readonly ProviderPreferencesService _prefs;
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
    private readonly Dictionary<string, (string? web, string? api)> _extractCache = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;

    public CoinSiteRegistry(AppDbContext db, ProviderCacheService providerCache,
        ProviderPreferencesService prefs, IConfiguration config, ILogger<CoinSiteRegistry> logger)
    {
        _db = db;
        _providerCache = providerCache;
        _prefs = prefs;
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
        var result = new Dictionary<string, CoinSiteDefinition>(StringComparer.OrdinalIgnoreCase);

        List<Models.Database.ProviderStorageEntity> providers =
            await _providerCache.GetCachedProvidersAsync(token).ConfigureAwait(false);

        foreach (var p in providers.Where(p => !string.IsNullOrEmpty(p.SourcePackageName)))
        {
            if (result.ContainsKey(p.Name))
                continue;
            if (!await IsCoinSourceAsync(p.SourcePackageName!, token).ConfigureAwait(false))
                continue;

            CoinSiteDefinition def = await BuildDefinitionAsync(p.Name, p.SourcePackageName!, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(def.Domain))
                result[p.Name] = def;
        }

        return result.Values.OrderBy(d => d.Provider).ToList();
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
        return all.FirstOrDefault(d => d.Provider.Equals(provider, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<bool> IsCoinSourceAsync(string pkg, CancellationToken token)
    {
        try
        {
            var prefs = await _prefs.GetProviderPreferencesAsync(pkg, token).ConfigureAwait(false);
            if (prefs == null)
                return false;
            foreach (var pref in prefs.Preferences)
            {
                string blob = (pref.Title ?? "") + " " + (pref.Summary ?? "");
                // A blocklist/filter preference is never a coin gate, even if it
                // happens to contain a coin word ("bLOCKED", "genre BLOCK").
                if (FilterPattern.IsMatch(blob))
                    continue;
                if (CoinPattern.IsMatch(blob))
                    return true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Coin check failed for {Pkg}", pkg);
        }
        return false;
    }

    private async Task<CoinSiteDefinition> BuildDefinitionAsync(string provider, string pkg, CancellationToken token)
    {
        // Local confirmed definition wins outright.
        lock (_lock)
        {
            if (_local.TryGetValue(provider, out var local) && local.Confirmed)
                return local;
        }

        (string? web, string? api) = ExtractHosts(pkg);
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
            foreach (string path in new[] { "/auth/login", "/login", "/api/auth/login", "/api/login", "/user/login", "/sign-in" })
                if (seen.Add(b + path))
                    yield return b + path;
    }

    /// <summary>
    /// Pulls the web host and API base URL from the extension APK by scanning
    /// its dex for https URLs. Cached per package (the APK doesn't change).
    /// </summary>
    private (string? web, string? api) ExtractHosts(string pkg)
    {
        lock (_lock)
        {
            if (_extractCache.TryGetValue(pkg, out var cached))
                return cached;
        }

        string? web = null, api = null;
        try
        {
            string? apk = FindApk(pkg);
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
            _logger.LogDebug(ex, "Host extraction failed for {Pkg}", pkg);
        }

        var pair = (web, api);
        lock (_lock) { _extractCache[pkg] = pair; }
        return pair;
    }

    private string? FindApk(string pkg)
    {
        try
        {
            string dir = Path.Combine(_extensionsDir, pkg);
            if (!Directory.Exists(dir))
                return null;
            return Directory.EnumerateFiles(dir, "*.apk", SearchOption.AllDirectories).FirstOrDefault();
        }
        catch { return null; }
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
