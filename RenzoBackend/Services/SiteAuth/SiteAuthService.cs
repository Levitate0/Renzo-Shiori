using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using Microsoft.EntityFrameworkCore;
using System.Net;
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
    private readonly ILogger _logger;

    public SiteAuthService(AppDbContext db, CookieJarBridge jar, SiteCredentialProtector protector,
        CoinSiteRegistry registry, ILogger<SiteAuthService> logger)
    {
        _db = db;
        _jar = jar;
        _protector = protector;
        _registry = registry;
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
        foreach (string loginUrl in _registry.CandidateLoginUrls(def))
        {
            foreach (string userField in userFields)
            {
                token.ThrowIfCancellationRequested();
                (bool ok, List<HarvestedCookie> harvested, string detail, bool endpointExists) =
                    await TryLoginAsync(def, loginUrl, userField, cred.Username, password, token).ConfigureAwait(false);
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

            // Try JSON first (these SPA APIs expect it), then form-encoded.
            HttpResponseMessage resp = await PostJsonAsync(http, loginUrl, fields, csrf, token).ConfigureAwait(false);
            if (resp.StatusCode == HttpStatusCode.UnsupportedMediaType || resp.StatusCode == HttpStatusCode.BadRequest)
            {
                resp.Dispose();
                resp = await http.PostAsync(loginUrl, new FormUrlEncodedContent(fields), token).ConfigureAwait(false);
            }

            bool endpointExists = resp.StatusCode != HttpStatusCode.NotFound && resp.StatusCode != HttpStatusCode.MethodNotAllowed;
            List<HarvestedCookie> harvested = FromContainer(jar, def.Domain);
            bool gotSession = harvested.Count > 0 && resp.IsSuccessStatusCode &&
                (string.IsNullOrEmpty(def.SessionCookieName) ||
                 harvested.Any(c => c.Name.Equals(def.SessionCookieName, StringComparison.OrdinalIgnoreCase)));

            string detail = resp.StatusCode switch
            {
                HttpStatusCode.NotFound => $"{Host(loginUrl)} has no login there.",
                HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity => "Login rejected — check the username/password.",
                _ when !resp.IsSuccessStatusCode => $"Login endpoint returned HTTP {(int)resp.StatusCode}.",
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
