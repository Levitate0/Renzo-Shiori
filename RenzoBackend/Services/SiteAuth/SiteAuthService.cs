using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RenzoBackend.Services.SiteAuth;

// Explicit camelCase names: the app sets PropertyNamingPolicy = null, so without
// these the record would serialize PascalCase and the frontend (reading .success/
// .detail) would treat every login — even a successful one — as a failure.
public record SiteLoginResult(
    [property: System.Text.Json.Serialization.JsonPropertyName("success")] bool Success,
    [property: System.Text.Json.Serialization.JsonPropertyName("status")] string Status,
    [property: System.Text.Json.Serialization.JsonPropertyName("detail")] string? Detail,
    [property: System.Text.Json.Serialization.JsonPropertyName("cookiesInjected")] int CookiesInjected);

/// <summary>
/// Owns coin/paid-site logins end-to-end: stores credentials (encrypted),
/// performs the login, harvests the resulting session cookies into the shared
/// Mihon cookie jar so the source's extension serves owned chapters, and
/// re-logs-in automatically when a session lapses. Also supports pasting a
/// session cookie directly for sites whose login can't be automated
/// (CAPTCHA / social sign-in).
/// </summary>
public class SiteAuthService
{
    private readonly AppDbContext _db;
    private readonly CookieJarBridge _jar;
    private readonly SiteCredentialProtector _protector;
    private readonly CoinSiteRegistry _registry;
    private readonly SettingsService _settings;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;

    public SiteAuthService(AppDbContext db, CookieJarBridge jar, SiteCredentialProtector protector,
        CoinSiteRegistry registry, SettingsService settings, IHttpClientFactory httpFactory, ILogger<SiteAuthService> logger)
    {
        _db = db;
        _jar = jar;
        _protector = protector;
        _registry = registry;
        _settings = settings;
        _httpFactory = httpFactory;
        _logger = logger;
    }

    private async Task<string?> DomainForAsync(string provider, CancellationToken token)
    {
        var def = await _registry.GetDefinitionAsync(provider, token).ConfigureAwait(false);
        return string.IsNullOrEmpty(def?.Domain) ? null : def!.Domain;
    }

    public async Task<List<SiteCredentialEntity>> ListAsync(Guid userId, CancellationToken token = default) =>
        await _db.SiteCredentials.Where(c => c.UserId == userId)
            .OrderBy(c => c.Provider).ToListAsync(token).ConfigureAwait(false);

    /// <summary>Creates or updates a credential and immediately attempts login.</summary>
    public async Task<(SiteCredentialEntity entity, SiteLoginResult result)> SaveAndLoginAsync(
        Guid userId, string provider, string username, string password, CancellationToken token = default)
    {
        SiteCredentialEntity? cred = await _db.SiteCredentials
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Provider == provider, token).ConfigureAwait(false);
        if (cred == null)
        {
            cred = new SiteCredentialEntity { Id = Guid.NewGuid(), UserId = userId, Provider = provider };
            _db.SiteCredentials.Add(cred);
        }
        cred.Username = username;
        if (!string.IsNullOrEmpty(password))
            cred.EncryptedPassword = _protector.Encrypt(password);

        SiteLoginResult result = await LoginAsync(cred, token).ConfigureAwait(false);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (cred, result);
    }

    /// <summary>Stores a manually-pasted session cookie header and injects it.</summary>
    public async Task<(SiteCredentialEntity entity, SiteLoginResult result)> SaveCookieAsync(
        Guid userId, string provider, string username, string cookieHeader, CancellationToken token = default)
    {
        string? domain = await DomainForAsync(provider, token).ConfigureAwait(false);
        if (domain == null)
            return (new SiteCredentialEntity(), new SiteLoginResult(false, "failed", "Couldn't determine this site's domain.", 0));

        List<HarvestedCookie> cookies = ParseCookieHeader(cookieHeader, domain);
        if (cookies.Count == 0)
            return (new SiteCredentialEntity(), new SiteLoginResult(false, "failed", "No cookies found in that value.", 0));

        SiteCredentialEntity? cred = await _db.SiteCredentials
            .FirstOrDefaultAsync(c => c.UserId == userId && c.Provider == provider, token).ConfigureAwait(false);
        if (cred == null)
        {
            cred = new SiteCredentialEntity { Id = Guid.NewGuid(), UserId = userId, Provider = provider };
            _db.SiteCredentials.Add(cred);
        }
        cred.Username = string.IsNullOrWhiteSpace(username) ? "(cookie)" : username;

        int injected = _jar.Inject(cookies);
        cred.EncryptedCookies = _protector.Encrypt(SerializeCookies(cookies));
        cred.Status = injected > 0 ? "manual_cookie" : "failed";
        cred.StatusDetail = injected > 0 ? $"{injected} cookies injected." : "Could not reach the cookie jar.";
        cred.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (cred, new SiteLoginResult(injected > 0, cred.Status, cred.StatusDetail, injected));
    }

    public async Task DeleteAsync(Guid userId, Guid id, CancellationToken token = default)
    {
        SiteCredentialEntity? cred = await _db.SiteCredentials
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, token).ConfigureAwait(false);
        if (cred == null)
            return;
        string? domain = await DomainForAsync(cred.Provider, token).ConfigureAwait(false);
        if (domain != null)
            _jar.ClearHost(domain);
        _db.SiteCredentials.Remove(cred);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs a form/JSON login for a credential, harvests cookies into the
    /// jar, and records status. Falls back to the cached cookie snapshot when
    /// the site has no automatable login.
    /// </summary>
    public async Task<SiteLoginResult> LoginAsync(SiteCredentialEntity cred, CancellationToken token = default)
    {
        CoinSiteDefinition? def = await _registry.GetDefinitionAsync(cred.Provider, token).ConfigureAwait(false);
        string? password = _protector.TryDecrypt(cred.EncryptedPassword);

        if (def == null || string.IsNullOrEmpty(def.Domain) || string.IsNullOrEmpty(password))
        {
            // Cookie-only credential (no password), or we couldn't discover the
            // site: re-inject the last known cookies so access survives a restart.
            int reinjected = ReinjectCached(cred);
            cred.Status = reinjected > 0 ? "manual_cookie" : "needs_login";
            cred.StatusDetail = reinjected > 0
                ? "Using saved cookies (paste a fresh cookie if chapters stop loading)."
                : "Add a username/password to log in, or paste a session cookie.";
            return new SiteLoginResult(reinjected > 0, cred.Status, cred.StatusDetail, reinjected);
        }

        // Try each candidate login URL × each username field guess until one
        // returns a session; persist the winning combination locally so future
        // logins go straight to it.
        List<string> userFields = new() { def.UsernameField };
        userFields.AddRange(CoinSiteRegistry.UsernameFieldGuesses.Where(f => !userFields.Contains(f)));

        string lastDetail = "No login endpoint responded.";
        SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        bool flareSolverrReady = settings.FlareSolverrEnabled && !string.IsNullOrWhiteSpace(settings.FlareSolverrUrl);

        foreach (string loginUrl in _registry.CandidateLoginUrls(def))
        {
            bool isConfirmedUrl = def.Confirmed && loginUrl == def.LoginUrl;
            foreach (string userField in userFields)
            {
                token.ThrowIfCancellationRequested();

                // Plain client first: it correctly captures Set-Cookie via .NET's
                // CookieContainer, and (confirmed by testing this exact site's login
                // endpoint directly, bypassing Renzo entirely) it isn't actually
                // Cloudflare-blocked here — a bare HTTP POST reaches the real login
                // form and gets a real session cookie back. FlareSolverr was tried
                // first originally on the assumption Cloudflare was the blocker (a
                // reasonable read of the symptoms at the time — see the coin-site
                // paywall doc on LockedChapterSupplementService, which DOES need it
                // for scraping), but its request.post here doesn't reliably surface
                // Set-Cookie headers even when the site demonstrably sets one — so
                // it's now only a fallback for sites the plain client genuinely can't
                // reach at all.
                (bool ok, List<HarvestedCookie> harvested, string detail, bool endpointExists) =
                    await TryLoginAsync(def, loginUrl, userField, cred.Username, password, token).ConfigureAwait(false);
                if (!ok && flareSolverrReady)
                {
                    var fsResult = await TryLoginViaFlareSolverrAsync(def, loginUrl, userField, cred.Username, password, settings, token).ConfigureAwait(false);
                    if (fsResult.ok)
                        (ok, harvested, detail, endpointExists) = fsResult;
                }
                if (ok)
                {
                    int injected = _jar.Inject(harvested);
                    cred.EncryptedCookies = _protector.Encrypt(SerializeCookies(harvested));
                    cred.LastLoginAt = DateTime.UtcNow;
                    cred.Status = injected > 0 ? "ok" : "failed";
                    cred.StatusDetail = injected > 0 ? $"Logged in, {injected} cookies active." : "Logged in but couldn't reach the cookie jar.";

                    // Persist the working endpoint + field as a confirmed local def.
                    def.LoginUrl = loginUrl;
                    def.UsernameField = userField;
                    def.Confirmed = true;
                    _registry.SaveLocal(def);

                    _logger.LogInformation("Site login {Provider}: ok via {Url} ({Field})", cred.Provider, loginUrl, userField);
                    return new SiteLoginResult(injected > 0, cred.Status, cred.StatusDetail, injected);
                }
                lastDetail = detail;
                // A wrong endpoint (404) is worth abandoning this URL; a rejected
                // credential means the endpoint is right but the field/creds are off.
                if (!endpointExists)
                    break;
            }

            // def.Confirmed means this URL previously logged in successfully — it's
            // not a guess, unlike the other candidates CandidateLoginUrls yields
            // (generic paths like /auth/login that were never going to match this
            // site). If every field guess on it just failed, trying those other
            // candidates next is pointless and actively harmful: their inevitable
            // 404 overwrites lastDetail, hiding the confirmed endpoint's real
            // failure reason behind an unrelated "no login there" for a URL nobody
            // was ever trying to use.
            if (isConfirmedUrl)
            {
                cred.Status = "failed";
                cred.StatusDetail = lastDetail + " If it keeps failing, paste a session cookie instead.";
                return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
            }
        }

        cred.Status = "failed";
        cred.StatusDetail = lastDetail + " If it keeps failing, paste a session cookie instead.";
        return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
    }

    private async Task<(bool ok, List<HarvestedCookie> cookies, string detail, bool endpointExists)> TryLoginAsync(
        CoinSiteDefinition def, string loginUrl, string userField, string username, string password, CancellationToken token)
    {
        try
        {
            var jar = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = jar, UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");

            // Pick up CSRF cookie/token if present.
            string? csrf = null;
            if (def.CsrfPageUrl != null)
            {
                try
                {
                    string page = await http.GetStringAsync(def.CsrfPageUrl, token).ConfigureAwait(false);
                    csrf = ExtractCsrf(page) ?? ExtractCookieValue(jar, def.Domain, "XSRF-TOKEN");
                }
                catch { /* no CSRF page — continue without */ }
            }

            var fields = new Dictionary<string, string> { [userField] = username, [def.PasswordField] = password };
            if (csrf != null) fields[def.CsrfField ?? "_token"] = csrf;
            if (!string.IsNullOrEmpty(def.SubmitField)) fields[def.SubmitField] = def.SubmitValue ?? "";
            if (def.CsrfPageUrl != null)
                http.DefaultRequestHeaders.Referrer = new Uri(def.CsrfPageUrl);

            // Sites with a real JSON REST API (ApiBase set, e.g. EZmanga's
            // vapi.* backend) expect a JSON body. Plain server-rendered sites
            // (ApiBase null, e.g. Violet Scans' WordPress theme form) only read
            // $_POST — a JSON body is silently ignored (PHP never parses it),
            // and since that still comes back as a normal 200 (not 400/415),
            // the old "JSON first, fall back on 400/415" logic never noticed:
            // the real credentials just never reached the server. Order by
            // which style this site actually is instead of always trying JSON
            // first.
            bool jsonFirst = !string.IsNullOrEmpty(def.ApiBase);
            HttpResponseMessage resp = jsonFirst
                ? await PostJsonAsync(http, loginUrl, fields, csrf, token).ConfigureAwait(false)
                : await http.PostAsync(loginUrl, new FormUrlEncodedContent(fields), token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.UnsupportedMediaType || resp.StatusCode == HttpStatusCode.BadRequest)
            {
                resp.Dispose();
                resp = jsonFirst
                    ? await http.PostAsync(loginUrl, new FormUrlEncodedContent(fields), token).ConfigureAwait(false)
                    : await PostJsonAsync(http, loginUrl, fields, csrf, token).ConfigureAwait(false);
            }

            bool endpointExists = resp.StatusCode != HttpStatusCode.NotFound && resp.StatusCode != HttpStatusCode.MethodNotAllowed;
            List<HarvestedCookie> harvested = FromContainer(jar, def.Domain);
            bool gotSession = harvested.Count > 0 && resp.IsSuccessStatusCode &&
                (string.IsNullOrEmpty(def.SessionCookieName) ||
                 harvested.Any(c => c.Name.Equals(def.SessionCookieName, StringComparison.OrdinalIgnoreCase)));

            // Echo the API's own error text. A bare status code hides the actual
            // reason — better-auth answers 403 with EMAIL_NOT_VERIFIED, "account
            // is using social login", banned, etc. — which is the difference
            // between a dead end and something the user can act on.
            string? apiError = await ReadApiErrorAsync(resp, token).ConfigureAwait(false);
            string With(string s) => apiError == null ? s : $"{s} — {apiError}";

            string detail = resp.StatusCode switch
            {
                HttpStatusCode.NotFound => $"{Host(loginUrl)} has no login there.",
                HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity => With("Login rejected — check the username/password."),
                _ when !resp.IsSuccessStatusCode => With($"Login endpoint returned HTTP {(int)resp.StatusCode}."),
                _ when !gotSession => "Login didn't set a session cookie.",
                _ => "ok",
            };
            resp.Dispose();
            return (gotSession, harvested, detail, endpointExists);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, new List<HarvestedCookie>(), "Couldn't reach " + Host(loginUrl) + ": " + ex.Message, false);
        }
    }

    /// <summary>
    /// Same login, but driven through FlareSolverr's real, JS-rendering browser
    /// instead of a bare HttpClient — needed for sites behind Cloudflare, where a
    /// plain POST can get back a 200 with SOME placeholder cookie (passing the old
    /// "did we get a cookie" check) without ever reaching the real login form, so
    /// the actual auth cookie (e.g. WordPress's wordpress_logged_in_*) never gets
    /// set even though login "looked" successful.
    /// </summary>
    private async Task<(bool ok, List<HarvestedCookie> cookies, string detail, bool endpointExists)> TryLoginViaFlareSolverrAsync(
        CoinSiteDefinition def, string loginUrl, string userField, string username, string password, SettingsDto settings, CancellationToken token)
    {
        string endpoint = settings.FlareSolverrUrl.TrimEnd('/') + "/v1";
        int ms = (int)Math.Clamp(settings.FlareSolverrTimeout.TotalMilliseconds, 15000, 120000);
        using HttpClient http = _httpFactory.CreateClient();
        http.Timeout = TimeSpan.FromMilliseconds(ms + 15000);

        try
        {
            // No CSRF pre-fetch here (unlike the plain-client path): this
            // FlareSolverr install has no session continuity between separate calls
            // (no sessions.create/destroy support), so a first GET can only ever
            // supply a token VALUE, never the matching cookie a real CSRF check
            // usually also wants — and def.CsrfField/CsrfPageUrl were auto-guessed
            // by CoinSiteRegistry, never confirmed necessary for this site. Skipping
            // it removes a second FlareSolverr round-trip (and its own failure/
            // timeout surface) for a step that was never verified to matter.
            var fields = new Dictionary<string, string> { [userField] = username, [def.PasswordField] = password };
            if (!string.IsNullOrEmpty(def.SubmitField)) fields[def.SubmitField] = def.SubmitValue ?? "";
            string postData = string.Join("&", fields.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));

            JsonElement? sol = await FlareSolverrCallAsync(http, endpoint,
                new { cmd = "request.post", url = loginUrl, postData, maxTimeout = ms }, token).ConfigureAwait(false);
            if (sol == null)
                return (false, new List<HarvestedCookie>(), "FlareSolverr couldn't reach the login endpoint.", true);

            var harvested = new List<HarvestedCookie>();
            if (sol.Value.TryGetProperty("cookies", out JsonElement cookiesEl) && cookiesEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement c in cookiesEl.EnumerateArray())
                {
                    string name = c.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name))
                        continue;
                    string value = c.TryGetProperty("value", out JsonElement v) ? v.GetString() ?? "" : "";
                    string domain = c.TryGetProperty("domain", out JsonElement d) ? d.GetString() ?? def.Domain : def.Domain;
                    string path = c.TryGetProperty("path", out JsonElement p) ? p.GetString() ?? "/" : "/";
                    harvested.Add(new HarvestedCookie(name, value, domain, path));
                }
            }

            int statusCode = sol.Value.TryGetProperty("status", out JsonElement st) && st.ValueKind == JsonValueKind.Number ? st.GetInt32() : 200;
            bool endpointExists = statusCode != 404 && statusCode != 405;
            bool gotSession = harvested.Count > 0 &&
                (string.IsNullOrEmpty(def.SessionCookieName) ||
                 harvested.Any(c => c.Name.Equals(def.SessionCookieName, StringComparison.OrdinalIgnoreCase)));

            string detail = !endpointExists ? $"{Host(loginUrl)} has no login there."
                : !gotSession ? "Login didn't set a session cookie (via FlareSolverr)."
                : "ok";
            return (gotSession, harvested, detail, endpointExists);
        }
        catch (Exception ex)
        {
            return (false, new List<HarvestedCookie>(), "FlareSolverr login failed: " + ex.Message, false);
        }
    }

    private static async Task<JsonElement?> FlareSolverrCallAsync(HttpClient http, string endpoint, object payload, CancellationToken token)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using HttpResponseMessage resp = await http.PostAsync(endpoint, content, token).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return null;
        await using Stream s = await resp.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using JsonDocument doc = await JsonDocument.ParseAsync(s, cancellationToken: token).ConfigureAwait(false);
        if (!doc.RootElement.TryGetProperty("solution", out JsonElement sol))
            return null;
        return sol.Clone();
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient http, string url,
        Dictionary<string, string> fields, string? csrf, CancellationToken token)
    {
        var content = new StringContent(JsonSerializer.Serialize(fields), System.Text.Encoding.UTF8, "application/json");
        if (csrf != null)
        {
            content.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);
            content.Headers.TryAddWithoutValidation("X-XSRF-TOKEN", csrf);
        }
        return await http.PostAsync(url, content, token).ConfigureAwait(false);
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? u) ? u.Host : url;

    /// <summary>
    /// Called when a page comes back locked/empty for a source: if we hold a
    /// credential for it, re-login and report whether cookies were refreshed.
    /// Best-effort and never throws into the caller's page fetch.
    /// </summary>
    public async Task<bool> EnsureLoggedInAsync(Guid userId, string provider, CancellationToken token = default)
    {
        try
        {
            SiteCredentialEntity? cred = await _db.SiteCredentials
                .FirstOrDefaultAsync(c => c.UserId == userId && c.Provider == provider, token).ConfigureAwait(false);
            if (cred == null)
                return false;
            SiteLoginResult r = await LoginAsync(cred, token).ConfigureAwait(false);
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            return r.Success;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Re-runs login for a stored credential by id and persists the result.</summary>
    public async Task<(SiteCredentialEntity? entity, SiteLoginResult result)> ReloginAsync(
        Guid userId, Guid id, CancellationToken token = default)
    {
        SiteCredentialEntity? cred = await _db.SiteCredentials
            .FirstOrDefaultAsync(c => c.Id == id && c.UserId == userId, token).ConfigureAwait(false);
        if (cred == null)
            return (null, new SiteLoginResult(false, "failed", "Not found.", 0));
        SiteLoginResult result = await LoginAsync(cred, token).ConfigureAwait(false);
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (cred, result);
    }

    /// <summary>On startup, re-inject cached cookies for every stored credential.</summary>
    public async Task RestoreAllAsync(CancellationToken token = default)
    {
        List<SiteCredentialEntity> all = await _db.SiteCredentials.ToListAsync(token).ConfigureAwait(false);
        int total = 0;
        foreach (SiteCredentialEntity cred in all)
            total += ReinjectCached(cred);
        if (total > 0)
            _logger.LogInformation("Restored {Count} site-login cookies into the shared jar", total);
    }

    private int ReinjectCached(SiteCredentialEntity cred)
    {
        string? json = _protector.TryDecrypt(cred.EncryptedCookies);
        if (json == null)
            return 0;
        try
        {
            var cookies = JsonSerializer.Deserialize<List<HarvestedCookie>>(json);
            return cookies == null ? 0 : _jar.Inject(cookies);
        }
        catch { return 0; }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private static string SerializeCookies(List<HarvestedCookie> cookies) => JsonSerializer.Serialize(cookies);

    /// <summary>
    /// The API's own error message from a failed login response, when it sent a
    /// JSON one. Best-effort and bounded — never throws, and never echoes an HTML
    /// block page back at the user.
    /// </summary>
    private static async Task<string?> ReadApiErrorAsync(HttpResponseMessage resp, CancellationToken token)
    {
        if (resp.IsSuccessStatusCode)
            return null;
        try
        {
            string body = (await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
            if (body.Length == 0 || body[0] != '{')
                return null;

            using JsonDocument doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (string key in new[] { "message", "error_description", "error", "code", "detail" })
            {
                if (doc.RootElement.TryGetProperty(key, out JsonElement el) &&
                    el.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(el.GetString()))
                {
                    string v = el.GetString()!.Trim();
                    return v.Length <= 160 ? v : v[..160] + "…";
                }
            }
        }
        catch { /* unparseable body — fall back to the bare status */ }
        return null;
    }

    private static List<HarvestedCookie> FromContainer(CookieContainer container, string domain)
    {
        var result = new List<HarvestedCookie>();
        foreach (Cookie c in container.GetAllCookies())
        {
            result.Add(new HarvestedCookie(c.Name, c.Value,
                string.IsNullOrEmpty(c.Domain) ? domain : c.Domain,
                string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Secure));
        }
        return result;
    }

    private static List<HarvestedCookie> ParseCookieHeader(string header, string domain)
    {
        var result = new List<HarvestedCookie>();
        foreach (string part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            string name = part[..eq].Trim();
            string value = part[(eq + 1)..].Trim();
            if (name.Length == 0)
                continue;
            result.Add(new HarvestedCookie(name, value, "." + domain));
        }
        return result;
    }

    private static string? ExtractCsrf(string html)
    {
        Match m = Regex.Match(html, "name=[\"']csrf-token[\"'][^>]*content=[\"']([^\"']+)", RegexOptions.IgnoreCase);
        if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        m = Regex.Match(html, "name=[\"']_token[\"'][^>]*value=[\"']([^\"']+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? ExtractCookieValue(CookieContainer container, string domain, string name)
    {
        foreach (Cookie c in container.GetAllCookies())
            if (c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(c.Value);
        return null;
    }
}
