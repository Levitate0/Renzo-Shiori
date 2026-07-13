using RensaioBackend.Data;
using RensaioBackend.Models.Database;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RensaioBackend.Services.SiteAuth;

public record SiteLoginResult(bool Success, string Status, string? Detail, int CookiesInjected);

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
    private readonly IHttpClientFactory _httpFactory;
    private readonly ILogger _logger;

    public SiteAuthService(AppDbContext db, CookieJarBridge jar, SiteCredentialProtector protector,
        IHttpClientFactory httpFactory, ILogger<SiteAuthService> logger)
    {
        _db = db;
        _jar = jar;
        _protector = protector;
        _httpFactory = httpFactory;
        _logger = logger;
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
        string? domain = SiteLoginDefinitions.DomainFor(provider);
        if (domain == null)
            return (new SiteCredentialEntity(), new SiteLoginResult(false, "failed", "Unknown site — no domain configured.", 0));

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
        string? domain = SiteLoginDefinitions.DomainFor(cred.Provider);
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
        SiteLoginDefinition? def = SiteLoginDefinitions.Get(cred.Provider);
        string? password = _protector.TryDecrypt(cred.EncryptedPassword);

        if (def == null || string.IsNullOrEmpty(password))
        {
            // No automatable login (or cookie-only credential): re-inject the
            // last known cookies so access survives a restart.
            int reinjected = ReinjectCached(cred);
            cred.Status = reinjected > 0 ? "manual_cookie" : "needs_login";
            cred.StatusDetail = reinjected > 0
                ? "Using saved cookies (this site needs a pasted cookie / manual login)."
                : "This site can't log in automatically — paste a session cookie.";
            return new SiteLoginResult(reinjected > 0, cred.Status, cred.StatusDetail, reinjected);
        }

        try
        {
            var cookieContainer = new CookieContainer();
            using var handler = new HttpClientHandler
            {
                CookieContainer = cookieContainer,
                UseCookies = true,
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = true
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            // Pick up a CSRF token/cookie if the site uses one.
            string? csrf = null;
            if (def.CsrfPageUrl != null)
            {
                string page = await http.GetStringAsync(def.CsrfPageUrl, token).ConfigureAwait(false);
                csrf = ExtractCsrf(page) ?? ExtractCookieValue(cookieContainer, def.Domain, "XSRF-TOKEN");
            }

            var form = new Dictionary<string, string>
            {
                [def.UsernameField] = cred.Username,
                [def.PasswordField] = password,
            };
            if (def.CsrfField != null && csrf != null)
                form[def.CsrfField] = csrf;

            using var resp = await http.PostAsync(def.LoginUrl, new FormUrlEncodedContent(form), token).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);

            // Harvest whatever cookies the login set, then inject into the shared jar.
            List<HarvestedCookie> harvested = FromContainer(cookieContainer, def.Domain);
            bool looksLoggedIn = harvested.Any(c =>
                string.IsNullOrEmpty(def.SessionCookieName) ||
                c.Name.Equals(def.SessionCookieName, StringComparison.OrdinalIgnoreCase))
                && resp.IsSuccessStatusCode;

            if (!looksLoggedIn)
            {
                cred.Status = "failed";
                cred.StatusDetail = resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.UnprocessableEntity
                    ? "Login rejected — check the username/password."
                    : $"Login didn't return a session (HTTP {(int)resp.StatusCode}). The site may need a pasted cookie instead.";
                return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
            }

            int injected = _jar.Inject(harvested);
            cred.EncryptedCookies = _protector.Encrypt(SerializeCookies(harvested));
            cred.LastLoginAt = DateTime.UtcNow;
            cred.Status = injected > 0 ? "ok" : "failed";
            cred.StatusDetail = injected > 0 ? $"Logged in, {injected} cookies active." : "Logged in but couldn't reach the cookie jar.";
            _logger.LogInformation("Site login {Provider}: {Status}", cred.Provider, cred.Status);
            return new SiteLoginResult(injected > 0, cred.Status, cred.StatusDetail, injected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Site login failed for {Provider}", cred.Provider);
            cred.Status = "failed";
            cred.StatusDetail = "Couldn't reach the site: " + ex.Message;
            return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
        }
    }

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
