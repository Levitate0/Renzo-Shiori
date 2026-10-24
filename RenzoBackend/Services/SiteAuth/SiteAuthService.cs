using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Providers;
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

    /// <summary>
    /// Stores a pasted session cookie header and injects it — the route for a
    /// login only a person can complete (a captcha, a social sign-in).
    /// </summary>
    public async Task<(SiteCredentialEntity entity, SiteLoginResult result)> SaveCookieAsync(
        Guid userId, string provider, string username, string cookieHeader, CancellationToken token = default)
    {
        string? domain = await DomainForAsync(provider, token).ConfigureAwait(false);
        if (domain == null)
            return (new SiteCredentialEntity(), new SiteLoginResult(false, "failed", "Couldn't determine this site's domain.", 0));

        // Never Cloudflare's own cookies: they are bound to the browser that
        // earned them, and would displace the server's clearance for this site.
        List<HarvestedCookie> cookies = ParseCookieHeader(cookieHeader, domain)
            .Where(c => !IsCloudflareCookie(c.Name))
            .ToList();
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

        // Verify the pasted cookie actually authenticates BEFORE injecting it —
        // otherwise an expired or wrong-site paste both overwrites the currently
        // working session in the live jar (same name+domain replaces in place) and
        // gets re-injected forever on every restart while chapters stay locked.
        // Reject only on a positive "not logged in" signal; a site we can't probe
        // (Unknown) is trusted, since a manual paste is deliberate.
        CoinSiteDefinition? cdef = await _registry.GetDefinitionAsync(provider, token).ConfigureAwait(false);
        bool unauth = cdef != null &&
            (await VerifySessionAsync(cdef, cookies, token).ConfigureAwait(false)).Verdict == SessionVerdict.Unauthenticated;
        if (unauth)
        {
            // Don't touch the live jar or the stored snapshot — leave whatever
            // session is currently working in place.
            cred.Status = "failed";
            cred.StatusDetail = "That cookie isn't a logged-in session for this site — copy it again after signing in.";
            cred.LastLoginAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            return (cred, new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0));
        }

        int injected = _jar.Inject(cookies);
        bool good = injected > 0;
        cred.EncryptedCookies = good ? _protector.Encrypt(SerializeCookies(cookies)) : cred.EncryptedCookies;
        cred.Status = good ? "manual_cookie" : "failed";
        cred.StatusDetail = good ? $"{injected} cookies injected." : "Could not reach the cookie jar.";
        cred.LastLoginAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
        return (cred, new SiteLoginResult(good, cred.Status, cred.StatusDetail, injected));
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
    /// the site has no automatable login, and keeps a browser sign-in going for
    /// a site whose login wants a captcha.
    /// </summary>
    /// <param name="interactive">
    /// False for background callers (the session refresh, a locked page). They
    /// never send the password to a login known to want a captcha: it can only
    /// fail there, and repeating it can get the account rate-limited.
    /// </param>
    public async Task<SiteLoginResult> LoginAsync(SiteCredentialEntity cred, CancellationToken token = default, bool interactive = true)
    {
        CoinSiteDefinition? def = await _registry.GetDefinitionAsync(cred.Provider, token).ConfigureAwait(false);
        string? password = _protector.TryDecrypt(cred.EncryptedPassword);

        if (def == null || string.IsNullOrEmpty(def.Domain) || string.IsNullOrEmpty(password))
        {
            // Cookie-only credential (no password), or we couldn't discover the
            // site: keep the saved session going while it still is one.
            if (def != null && !string.IsNullOrEmpty(def.Domain))
            {
                SiteLoginResult? kept = await CheckSessionAsync(cred, def, token).ConfigureAwait(false);
                if (kept != null)
                    return kept;
            }
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

        // The most telling failure so far: a later guess's 404 must not replace
        // what the real login endpoint said.
        string lastDetail = "No login endpoint responded.";
        int lastWeight = 0;
        void Note(string detail, Outcome kind)
        {
            if (Weight(kind) >= lastWeight)
            {
                lastDetail = detail;
                lastWeight = Weight(kind);
            }
        }
        SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        bool flareSolverrReady = settings.FlareSolverrEnabled && !string.IsNullOrWhiteSpace(settings.FlareSolverrUrl);

        // Set when the confirmed URL logs in but the session doesn't verify —
        // i.e. the stored "confirmed" endpoint was pinned by the OLD unverified
        // logic and is actually wrong (e.g. a site's HTML /login page rather than
        // its JSON /api/auth/login). In that case the short-circuit below must NOT
        // fire, or re-discovery never reaches the real endpoint.
        bool confirmedUrlUnverified = false;

        // Verification costs a handful of GETs, so it is bounded three ways or a
        // catch-all SPA (which hands a 2xx + visitor cookie on EVERY candidate
        // path and renders isAuthenticated:false) would turn one login into
        // thousands of requests:
        //   • memoize the verdict by cookie signature — every catch-all path
        //     yields the SAME anonymous cookie, so it is verified once, not once
        //     per candidate;
        //   • an unauthenticated result BREAKS to the next URL rather than trying
        //     more username fields on it (all fields on a catch-all path behave
        //     identically — a real endpoint rejects a wrong field with a 4xx,
        //     which lands on the !ok path and does keep trying fields);
        //   • a hard ceiling on total login POSTs as a backstop.
        var verifyCache = new Dictionary<string, SessionVerdict>();
        var retriedSignatures = new HashSet<string>();
        int loginAttempts = 0;
        const int MaxLoginAttempts = 24;

        static string Signature(List<HarvestedCookie> h) =>
            string.Join("&", h.Select(c => c.Name + "=" + c.Value).OrderBy(x => x, StringComparer.Ordinal));

        async Task<SessionVerdict> VerifyCachedAsync(List<HarvestedCookie> h)
        {
            string sig = Signature(h);
            if (verifyCache.TryGetValue(sig, out SessionVerdict cached))
                return cached;
            SessionVerdict v = (await VerifySessionAsync(def, h, token).ConfigureAwait(false)).Verdict;
            verifyCache[sig] = v;
            return v;
        }

        async Task<SiteLoginResult> AcceptAsync(string url, string field, List<HarvestedCookie> h)
        {
            int inj = _jar.Inject(h);
            cred.EncryptedCookies = _protector.Encrypt(SerializeCookies(h));
            cred.LastLoginAt = DateTime.UtcNow;
            cred.Status = inj > 0 ? "ok" : "failed";
            cred.StatusDetail = inj > 0 ? $"Logged in, {inj} cookies active." : "Logged in but couldn't reach the cookie jar.";
            // Persist the working endpoint + field. Only reached once the session
            // verified (or was unprobeable), so a false positive is never pinned.
            def.LoginUrl = url;
            def.UsernameField = field;
            def.Confirmed = true;
            def.CaptchaRequired = false;
            _registry.SaveLocal(def);
            _logger.LogInformation("Site login {Provider}: ok via {Url} ({Field})", cred.Provider, url, field);
            return new SiteLoginResult(inj > 0, cred.Status, cred.StatusDetail, inj);
        }

        // A login that wants a captcha: the session a person signed in to is
        // what counts, so keep that going. The password gets a single try, in
        // case the site has dropped its captcha since.
        if (def.CaptchaRequired)
        {
            SiteLoginResult? kept = await CheckSessionAsync(cred, def, token).ConfigureAwait(false);
            if (kept is { Success: true })
                return kept;
            if (!interactive)
                return kept ?? NeedsPerson(cred, ended: false);
            if (!string.IsNullOrEmpty(def.LoginUrl))
            {
                loginAttempts++;
                (bool ok, List<HarvestedCookie> harvested, string detail, bool exists, Outcome kind) =
                    await TryLoginAsync(def, def.LoginUrl, def.UsernameField, cred.Username, password, token).ConfigureAwait(false);
                if (ok && await VerifyCachedAsync(harvested) != SessionVerdict.Unauthenticated)
                    return await AcceptAsync(def.LoginUrl, def.UsernameField, harvested).ConfigureAwait(false);
                if (kind == Outcome.Captcha || !exists)
                    return NeedsPerson(cred, ended: kept != null);
                // The login changed shape: search again below.
                Note(detail, kind);
            }
            def.CaptchaRequired = false;
        }

        // Follow a site that moved ahead of its extension: a login POST doesn't
        // survive the redirect (it arrives as a GET, without the credentials).
        await FollowSiteMoveAsync(cred.Provider, def, token).ConfigureAwait(false);

        foreach (string loginUrl in _registry.CandidateLoginUrls(def))
        {
            if (loginAttempts >= MaxLoginAttempts) break;
            bool isConfirmedUrl = def.Confirmed && loginUrl == def.LoginUrl;
            foreach (string userField in userFields)
            {
                if (loginAttempts >= MaxLoginAttempts) break;
                token.ThrowIfCancellationRequested();

                // Plain client first: it correctly captures Set-Cookie via .NET's
                // CookieContainer and reaches these login endpoints directly;
                // FlareSolverr is only a fallback for sites the plain client can't
                // reach at all (its request.post doesn't reliably surface
                // Set-Cookie even when the site sets one).
                loginAttempts++;
                (bool ok, List<HarvestedCookie> harvested, string detail, bool endpointExists, Outcome kind) =
                    await TryLoginAsync(def, loginUrl, userField, cred.Username, password, token).ConfigureAwait(false);
                if (kind == Outcome.Captcha)
                {
                    // The right endpoint, and only a person can get past it.
                    def.LoginUrl = loginUrl;
                    def.UsernameField = userField;
                    def.CaptchaRequired = true;
                    def.Confirmed = false;
                    _registry.SaveLocal(def);
                    _logger.LogInformation("Site login {Provider}: {Url} asks for a captcha, so a person has to sign in.",
                        cred.Provider, loginUrl);
                    SiteLoginResult? kept = await CheckSessionAsync(cred, def, token).ConfigureAwait(false);
                    return kept is { Success: true } ? kept : NeedsPerson(cred, ended: kept != null);
                }
                if (!ok && flareSolverrReady)
                {
                    var fsResult = await TryLoginViaFlareSolverrAsync(def, loginUrl, userField, cred.Username, password, settings, token).ConfigureAwait(false);
                    if (fsResult.ok)
                        (ok, harvested, detail, endpointExists, kind) = fsResult;
                }
                if (ok)
                {
                    // "ok" so far only means the endpoint took the POST and set a
                    // cookie — which sites hand to anonymous visitors too, and
                    // which PHP form handlers return on bad creds. Verify the
                    // session actually authenticates before trusting it. Only a
                    // POSITIVE "not logged in" signal rejects; Unknown is accepted,
                    // so a site this can't probe is never worse off than before.
                    if (await VerifyCachedAsync(harvested) != SessionVerdict.Unauthenticated)
                        return await AcceptAsync(loginUrl, userField, harvested).ConfigureAwait(false);

                    // Unauthenticated. Retry ONCE with the opposite body encoding —
                    // the general form of every past per-site "JSON vs form" fix (a
                    // JSON API reached with a form body, or a $_POST form reached
                    // with JSON, answers 200 and drops the creds). Gated on a novel
                    // cookie signature so a catch-all path we've already flipped
                    // isn't flipped again on the next field.
                    if (loginAttempts < MaxLoginAttempts && retriedSignatures.Add(Signature(harvested)))
                    {
                        bool primaryJson = !string.IsNullOrEmpty(def.ApiBase);
                        loginAttempts++;
                        (bool ok2, List<HarvestedCookie> harvested2, _, _, _) =
                            await TryLoginAsync(def, loginUrl, userField, cred.Username, password, token, forceJson: !primaryJson).ConfigureAwait(false);
                        if (ok2 && await VerifyCachedAsync(harvested2) != SessionVerdict.Unauthenticated)
                            return await AcceptAsync(loginUrl, userField, harvested2).ConfigureAwait(false);
                    }

                    Note("Signed in, but the session came back unauthenticated — check the username/password.", Outcome.Unverified);
                    if (isConfirmedUrl) confirmedUrlUnverified = true;
                    // Every username field on a catch-all path yields the same
                    // visitor session, so don't try the rest — move to the next URL.
                    break;
                }
                Note(detail, kind);
                // A wrong endpoint (404) is worth abandoning this URL; a rejected
                // credential means the endpoint is right but the field/creds are off.
                if (!endpointExists)
                    break;
            }

            // A confirmed URL that only produced FAILED logins short-circuits the
            // rest (its candidates are guesses whose 404s would bury the real
            // error) — UNLESS its only failure was that the session didn't verify,
            // which means the confirmation is stale (pinned before verification
            // existed): drop it and let the remaining candidates, including the
            // real endpoint, be tried.
            if (isConfirmedUrl && !confirmedUrlUnverified)
            {
                cred.Status = "failed";
                cred.StatusDetail = lastDetail + " If it keeps failing, paste a session cookie instead.";
                return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
            }
            if (isConfirmedUrl && confirmedUrlUnverified)
                def.Confirmed = false;
        }

        cred.Status = "failed";
        cred.StatusDetail = lastDetail + " If it keeps failing, paste a session cookie instead.";
        return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
    }

    private const string SignInAdvice =
        "Sign in on the site in your browser, then paste its session cookie here (Method: Paste session cookie).";

    /// <summary>A login only a person can complete: say so, and what to do instead.</summary>
    private static SiteLoginResult NeedsPerson(SiteCredentialEntity cred, bool ended)
    {
        cred.Status = "captcha";
        cred.StatusDetail = (ended ? $"Your {cred.Provider} session has ended. The site" : cred.Provider) +
            " asks for a captcha when you sign in, so Renzo Shiori can't sign in for you. " + SignInAdvice;
        return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
    }

    /// <summary>
    /// A site that answers its old address with a redirect to a new one has
    /// moved, whether or not its extension has caught up yet. Sign in there:
    /// the redirect would turn the login POST into a GET and drop the
    /// credentials (Violet Scans, 2026-09-28 onwards).
    /// </summary>
    private async Task FollowSiteMoveAsync(string provider, CoinSiteDefinition def, CancellationToken token)
    {
        try
        {
            using var handler = new HttpClientHandler { AllowAutoRedirect = true, AutomaticDecompression = DecompressionMethods.All };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            using HttpResponseMessage resp = await http.GetAsync("https://" + def.Domain.TrimStart('.') + "/",
                HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            string? landed = resp.RequestMessage?.RequestUri?.Host;
            // A redirect to an error page is not a move.
            if (!resp.IsSuccessStatusCode || string.IsNullOrEmpty(landed) ||
                SourceHostsService.Normalize(landed) == SourceHostsService.Normalize(def.Domain))
                return;
            string old = def.Domain;
            CoinSiteRegistry.RebaseTo(def, landed.ToLowerInvariant());
            _registry.SaveLocal(def);
            _logger.LogInformation("Site login {Provider}: {Old} now redirects to {New}; signing in there.", provider, old, def.Domain);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { /* unreachable root: sign in where we were going to */ }
    }

    /// <summary>
    /// Checks a stored cookie session (a browser sign-in or a pasted cookie)
    /// and keeps it current: re-injected while it is still a session, refreshed
    /// when the site rolls its cookies on use, reported once it has ended.
    /// Rolling sessions (better-auth's included) are extended by exactly this
    /// kind of use, so a regular check is also what keeps one alive. Returns
    /// null when nothing is stored to check.
    /// </summary>
    public async Task<SiteLoginResult?> CheckSessionAsync(SiteCredentialEntity cred, CoinSiteDefinition def, CancellationToken token = default)
    {
        List<HarvestedCookie> stored = StoredCookies(cred);
        if (stored.Count == 0)
            return null;
        (SessionVerdict verdict, List<HarvestedCookie>? renewed) = await VerifySessionAsync(def, stored, token).ConfigureAwait(false);
        if (verdict == SessionVerdict.Unauthenticated)
        {
            if (def.CaptchaRequired)
                return NeedsPerson(cred, ended: true);
            cred.Status = string.IsNullOrEmpty(cred.EncryptedPassword) ? "needs_login" : "failed";
            cred.StatusDetail = $"Your {cred.Provider} session has ended. " + SignInAdvice;
            return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
        }
        List<HarvestedCookie> live = renewed ?? stored;
        if (renewed != null)
            cred.EncryptedCookies = _protector.Encrypt(SerializeCookies(renewed));
        int injected = _jar.Inject(live);
        if (injected == 0)
        {
            cred.Status = "failed";
            cred.StatusDetail = "Could not reach the cookie jar.";
            return new SiteLoginResult(false, cred.Status, cred.StatusDetail, 0);
        }
        bool wasBroken = cred.Status is not ("ok" or "manual_cookie");
        cred.Status = "manual_cookie";
        cred.LastLoginAt = DateTime.UtcNow;
        if (wasBroken || string.IsNullOrEmpty(cred.StatusDetail))
            cred.StatusDetail = verdict == SessionVerdict.Authenticated
                ? $"Signed in, {injected} cookies active."
                : "Using saved cookies (paste a fresh cookie if chapters stop loading).";
        return new SiteLoginResult(true, cred.Status, cred.StatusDetail, injected);
    }

    private List<HarvestedCookie> StoredCookies(SiteCredentialEntity cred)
    {
        string? json = _protector.TryDecrypt(cred.EncryptedCookies);
        if (json == null)
            return new List<HarvestedCookie>();
        try
        {
            return JsonSerializer.Deserialize<List<HarvestedCookie>>(json) ?? new List<HarvestedCookie>();
        }
        catch
        {
            return new List<HarvestedCookie>();
        }
    }

    /// <summary>
    /// After a source moved domain (see SourceMoveService): its logins were made
    /// on the old host. A saved password signs in again on the new one; a cookie
    /// session can't move with it, so it is marked for a fresh sign-in.
    /// </summary>
    public async Task OnSourceMovedAsync(string provider, string newHost, CancellationToken token = default)
    {
        List<SiteCredentialEntity> creds = await _db.SiteCredentials
            .Where(c => c.Provider == provider).ToListAsync(token).ConfigureAwait(false);
        foreach (SiteCredentialEntity cred in creds)
        {
            if (!string.IsNullOrEmpty(cred.EncryptedPassword))
            {
                SiteLoginResult r = await LoginAsync(cred, token, interactive: false).ConfigureAwait(false);
                _logger.LogInformation("Site login {Provider} after its move to {Host}: {Detail}", provider, newHost, r.Detail);
            }
            else
            {
                // A session already made on the new host (signed in again since
                // the move) is checked, not thrown away.
                string target = SourceHostsService.Normalize(newHost);
                bool onNewHost = StoredCookies(cred).Any(c =>
                {
                    string d = SourceHostsService.Normalize(c.Domain.TrimStart('.'));
                    return d == target || target.EndsWith("." + d, StringComparison.Ordinal);
                });
                CoinSiteDefinition? def = onNewHost
                    ? await _registry.GetDefinitionAsync(provider, token).ConfigureAwait(false) : null;
                if (def != null && await CheckSessionAsync(cred, def, token).ConfigureAwait(false) is { Success: true })
                    continue;
                cred.Status = "needs_login";
                cred.StatusDetail = $"{provider} moved to {newHost}. " + SignInAdvice;
            }
        }
        await _db.SaveChangesAsync(token).ConfigureAwait(false);
    }

    private async Task<(bool ok, List<HarvestedCookie> cookies, string detail, bool endpointExists, Outcome kind)> TryLoginAsync(
        CoinSiteDefinition def, string loginUrl, string userField, string username, string password, CancellationToken token,
        bool? forceJson = null)
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
            // Which body encoding to send first. Normally inferred from whether
            // the site has a JSON API host, but the caller can force the opposite
            // when a first attempt logged in "on paper" yet didn't authenticate —
            // the tell-tale of a content-type mismatch (JSON body silently ignored
            // by a $_POST form handler, or a form body ignored by a JSON API).
            bool jsonFirst = forceJson ?? !string.IsNullOrEmpty(def.ApiBase);
            HttpResponseMessage resp = jsonFirst
                ? await PostJsonAsync(http, loginUrl, fields, csrf, token).ConfigureAwait(false)
                : await http.PostAsync(loginUrl, new FormUrlEncodedContent(fields), token).ConfigureAwait(false);
            // A body the endpoint can't parse in the encoding we chose: flip to
            // the other one. JSON APIs reject a form body with 415, 400, or a
            // validation 422 (Laravel/Node), and 406 when they'll only emit JSON;
            // covering all four is what lets a JSON login be found on a site with
            // no detectable ApiBase (so jsonFirst started false) without a per-
            // site rule — e.g. a Next.js site whose real endpoint is /api/auth/login.
            if (resp.StatusCode is HttpStatusCode.UnsupportedMediaType or HttpStatusCode.BadRequest
                or HttpStatusCode.UnprocessableEntity or HttpStatusCode.NotAcceptable)
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
                 harvested.Any(c => IsSessionCookie(def, c.Name)));

            // Echo the API's own error text. A bare status code hides the actual
            // reason — better-auth answers 403 with EMAIL_NOT_VERIFIED, "account
            // is using social login", banned, etc. — which is the difference
            // between a dead end and something the user can act on.
            string? apiError = await ReadApiErrorAsync(resp, token).ConfigureAwait(false);
            string With(string s) => apiError == null ? s : $"{s} — {apiError}";

            // "Complete the captcha" means this IS the login, and only a person
            // can get past it. It used to read as one more failed guess, so the
            // search carried on and reported whatever the last candidate said
            // (Vortex Scans: "Please complete captcha" became "HTTP 405").
            string body = await ReadBodyAsync(resp, token).ConfigureAwait(false);
            if (AsksForCaptcha(resp, body, gotSession))
            {
                resp.Dispose();
                return (false, harvested, "captcha", true, Outcome.Captcha);
            }

            (string detail, Outcome kind) = resp.StatusCode switch
            {
                HttpStatusCode.NotFound => ($"{Host(loginUrl)} has no login there.", Outcome.NotThere),
                HttpStatusCode.MethodNotAllowed => (With($"Login endpoint returned HTTP {(int)resp.StatusCode}."), Outcome.NotThere),
                HttpStatusCode.Unauthorized or HttpStatusCode.UnprocessableEntity => (With("Login rejected — check the username/password."), Outcome.Rejected),
                // A JSON API that explains a refusal (wrong password, unverified
                // email, banned) is the real login talking.
                _ when !resp.IsSuccessStatusCode && apiError != null && (int)resp.StatusCode is >= 400 and < 500 =>
                    (With($"Login endpoint returned HTTP {(int)resp.StatusCode}."), Outcome.Rejected),
                _ when !resp.IsSuccessStatusCode => (With($"Login endpoint returned HTTP {(int)resp.StatusCode}."), Outcome.Http),
                _ when !gotSession => ("Login didn't set a session cookie.", Outcome.NoSession),
                _ => ("ok", Outcome.Ok),
            };
            resp.Dispose();
            return (gotSession, harvested, detail, endpointExists, kind);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, new List<HarvestedCookie>(), "Couldn't reach " + Host(loginUrl) + ": " + ex.Message, false, Outcome.Unreachable);
        }
    }

    /// <summary>
    /// Whether a cookie is this site's session cookie. WordPress suffixes its
    /// logged-in cookie with md5(site URL), so any wordpress_logged_in_* counts:
    /// the suffix changes whenever the site's address does.
    /// </summary>
    private static bool IsSessionCookie(CoinSiteDefinition def, string name)
    {
        const string WordPress = "wordpress_logged_in_";
        if (name.Equals(def.SessionCookieName, StringComparison.OrdinalIgnoreCase))
            return true;
        return def.SessionCookieName.StartsWith(WordPress, StringComparison.OrdinalIgnoreCase) &&
               name.StartsWith(WordPress, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>What a failed attempt says about the login, strongest first in <see cref="Weight"/>.</summary>
    private enum Outcome { Ok, Captcha, Rejected, Unverified, NoSession, Http, Unreachable, NotThere }

    /// <summary>
    /// How much a failure tells the user. The search tries many guessed URLs;
    /// a later guess's 404 or 405 must never replace what the real login said.
    /// </summary>
    private static int Weight(Outcome o) => o switch
    {
        Outcome.Captcha => 7,
        Outcome.Rejected => 6,
        Outcome.Unverified => 5,
        Outcome.NoSession => 4,
        Outcome.Http => 3,
        Outcome.Unreachable => 2,
        _ => 1,
    };

    private static readonly Regex CaptchaWords = new(
        @"\b(captcha|recaptcha|hcaptcha|turnstile|humanity)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CaptchaWidget = new(
        // The widget containers only: a site can load a captcha script on every
        // page for its comment box, and that alone doesn't gate the login.
        @"class=[""'][^""']*\b(cf-turnstile|g-recaptcha|h-captcha)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// True when the login wants a captcha: an API that refuses with a captcha
    /// message, or a form page that came back with a captcha widget and no
    /// session. A Cloudflare challenge page also talks about Turnstile, but that
    /// guards the whole site, not the login, and has its own route (the solver),
    /// so it is not counted.
    /// </summary>
    private static bool AsksForCaptcha(HttpResponseMessage resp, string body, bool gotSession)
    {
        if (gotSession || body.Length == 0)
            return false;
        bool siteChallenge = resp.Headers.Contains("cf-mitigated") ||
            body.Contains("Just a moment", StringComparison.OrdinalIgnoreCase) ||
            body.Contains("cf_chl_opt", StringComparison.Ordinal);
        if (siteChallenge)
            return false;
        if (!resp.IsSuccessStatusCode && body.Length <= 4096 && CaptchaWords.IsMatch(body))
            return true;
        return CaptchaWidget.IsMatch(body);
    }

    /// <summary>The response body, bounded; empty when it can't be read.</summary>
    private static async Task<string> ReadBodyAsync(HttpResponseMessage resp, CancellationToken token)
    {
        try
        {
            string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            return body.Length > 512_000 ? body[..512_000] : body;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return ""; }
    }

    /// <summary>
    /// Same login, but driven through FlareSolverr's real, JS-rendering browser
    /// instead of a bare HttpClient — needed for sites behind Cloudflare, where a
    /// plain POST can get back a 200 with SOME placeholder cookie (passing the old
    /// "did we get a cookie" check) without ever reaching the real login form, so
    /// the actual auth cookie (e.g. WordPress's wordpress_logged_in_*) never gets
    /// set even though login "looked" successful.
    /// </summary>
    private async Task<(bool ok, List<HarvestedCookie> cookies, string detail, bool endpointExists, Outcome kind)> TryLoginViaFlareSolverrAsync(
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
                return (false, new List<HarvestedCookie>(), "FlareSolverr couldn't reach the login endpoint.", true, Outcome.Unreachable);

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
                 harvested.Any(c => IsSessionCookie(def, c.Name)));

            string detail = !endpointExists ? $"{Host(loginUrl)} has no login there."
                : !gotSession ? "Login didn't set a session cookie (via FlareSolverr)."
                : "ok";
            Outcome kind = !endpointExists ? Outcome.NotThere : !gotSession ? Outcome.NoSession : Outcome.Ok;
            return (gotSession, harvested, detail, endpointExists, kind);
        }
        catch (Exception ex)
        {
            return (false, new List<HarvestedCookie>(), "FlareSolverr login failed: " + ex.Message, false, Outcome.Unreachable);
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
            SiteLoginResult r = await LoginAsync(cred, token, interactive: false).ConfigureAwait(false);
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            return r.Success;
        }
        catch
        {
            return false;
        }
    }

    // A relogin does a full credential POST. Anything that reacts to a locked
    // page (the reader poll, a batch of locked downloads) could otherwise fire
    // one per attempt and hammer the site's login endpoint. Gate to one attempt
    // per user+provider per minute and replay the last verdict within the window.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime at, bool ok)> ReloginGate = new();
    // Per-key lock so a CONCURRENT burst (e.g. a batch of locked chapters from
    // one series all hitting the paywall at once) single-flights instead of each
    // reading an empty gate and launching its own login. Without this the gate is
    // a check-then-act race — it only throttles AFTER the first result is written.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> ReloginLocks = new();

    /// <summary>
    /// <see cref="EnsureLoggedInAsync"/> with a shared 60-second per-user-per-
    /// provider rate gate, so callers reacting to lock errors don't spam logins.
    /// Concurrent callers for the same key serialize and share one login's result.
    /// </summary>
    public async Task<bool> EnsureLoggedInRateLimitedAsync(Guid userId, string provider, CancellationToken token = default)
    {
        string key = userId + ":" + provider;
        if (ReloginGate.TryGetValue(key, out var cached) && DateTime.UtcNow - cached.at < TimeSpan.FromSeconds(60))
            return cached.ok;

        SemaphoreSlim gate = ReloginLocks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            // Re-check inside the lock: a caller that queued behind the first one
            // now sees its fresh result and skips a redundant login POST.
            if (ReloginGate.TryGetValue(key, out var g) && DateTime.UtcNow - g.at < TimeSpan.FromSeconds(60))
                return g.ok;
            bool ok = await EnsureLoggedInAsync(userId, provider, token).ConfigureAwait(false);
            ReloginGate[key] = (DateTime.UtcNow, ok);
            return ok;
        }
        finally { gate.Release(); }
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

    // ── session verification ────────────────────────────────────────────

    private enum SessionVerdict { Authenticated, Unauthenticated, Unknown }

    /// <summary>
    /// Answers "is this cookie set actually a logged-in session?" without any
    /// per-site knowledge, so one check covers every stack. It probes with the
    /// harvested cookies and looks for GENERIC signals:
    ///   • a WordPress <c>wordpress_logged_in_*</c> cookie (prefix, since the
    ///     suffix is a per-site hash) → authenticated;
    ///   • a JSON session/user endpoint returning a user object → authenticated,
    ///     or 401 / a null-user body → not;
    ///   • a page whose Next.js/SPA payload carries <c>"isAuthenticated":true</c>
    ///     / a logout affordance → authenticated, or <c>"isAuthenticated":false</c>
    ///     / a bare sign-in page → not.
    /// Returns <see cref="SessionVerdict.Unknown"/> when nothing decisive is seen
    /// — callers treat Unknown as "accept", so a site this can't read is never
    /// worse off than before verification existed.
    /// </summary>
    private async Task<(SessionVerdict Verdict, List<HarvestedCookie>? Renewed)> VerifySessionAsync(
        CoinSiteDefinition def, List<HarvestedCookie> cookies, CancellationToken token)
    {
        var jar = new CookieContainer();
        SessionVerdict verdict = await VerifyWithJarAsync(def, cookies, jar, token).ConfigureAwait(false);
        return (verdict, verdict == SessionVerdict.Authenticated ? Renewed(cookies, jar) : null);
    }

    /// <summary>
    /// The stored cookies with whatever the site replaced while being checked
    /// (a rolling session re-issues its cookie on use), or null when nothing
    /// changed. Never drops a stored cookie, and never takes Cloudflare's own:
    /// those belong to the solver's browser, not to the login.
    /// </summary>
    private static List<HarvestedCookie>? Renewed(List<HarvestedCookie> stored, CookieContainer jar)
    {
        var result = stored.ToList();
        bool changed = false;
        foreach (Cookie c in jar.GetAllCookies())
        {
            if (string.IsNullOrEmpty(c.Value) || c.Expired || IsCloudflareCookie(c.Name))
                continue;
            string d = c.Domain.TrimStart('.');
            int i = result.FindIndex(x => x.Name == c.Name &&
                string.Equals(x.Domain.TrimStart('.'), d, StringComparison.OrdinalIgnoreCase));
            if (i >= 0)
            {
                if (result[i].Value != c.Value)
                {
                    result[i] = result[i] with { Value = c.Value };
                    changed = true;
                }
            }
            else if (!result.Any(x => x.Name == c.Name))
            {
                result.Add(new HarvestedCookie(c.Name, c.Value, c.Domain, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, c.Secure));
                changed = true;
            }
        }
        return changed ? result : null;
    }

    /// <summary>
    /// Cloudflare's bot-management cookies (cf_clearance, __cf_bm, _cfuvid, …).
    /// They are bound to the browser and network that earned them, so one taken
    /// from a phone or a desktop browser would replace the server's own and get
    /// the server challenged on that site.
    /// </summary>
    private static bool IsCloudflareCookie(string name) =>
        name.StartsWith("cf_", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("__cf", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("_cfuvid", StringComparison.OrdinalIgnoreCase);

    private async Task<SessionVerdict> VerifyWithJarAsync(
        CoinSiteDefinition def, List<HarvestedCookie> cookies, CookieContainer jar, CancellationToken token)
    {
        try
        {
            // WordPress: the logged-in cookie is definitive and needs no request.
            if (cookies.Any(c => c.Name.StartsWith("wordpress_logged_in_", StringComparison.OrdinalIgnoreCase)))
                return SessionVerdict.Authenticated;

            string? domain = def.Domain?.TrimStart('.');
            if (string.IsNullOrEmpty(domain))
                return SessionVerdict.Unknown;

            foreach (HarvestedCookie c in cookies)
            {
                string host = string.IsNullOrEmpty(c.Domain) ? domain : c.Domain.TrimStart('.');
                try { jar.Add(new Uri("https://" + host + "/"), new Cookie(c.Name, c.Value, string.IsNullOrEmpty(c.Path) ? "/" : c.Path, host)); }
                catch { /* skip a cookie that won't scope */ }
            }
            using var handler = new HttpClientHandler { CookieContainer = jar, UseCookies = true, AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd(
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/html, */*");

            // Build session/user probe URLs from BOTH the api base and the host,
            // deduped. Two path shapes are tried so a base that already contains a
            // path segment doesn't double it: SHORT paths (/get-session, /me) are
            // appended to ApiBase — e.g. ApiBase "https://api.magustoon.org/api/auth"
            // + "/get-session"; ABSOLUTE paths (/api/auth/get-session) are appended
            // to the host root. The old code appended the absolute form to ApiBase,
            // producing ".../api/auth/api/auth/get-session" (404) and losing the
            // one positive signal better-auth sites give.
            var probeUrls = new List<string>();
            var seenProbe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void AddProbe(string u) { if (seenProbe.Add(u)) probeUrls.Add(u); }
            string hostBase = "https://" + domain;
            if (!string.IsNullOrEmpty(def.ApiBase))
                foreach (string sp in new[] { "/get-session", "/session", "/me", "/user" })
                    AddProbe(def.ApiBase!.TrimEnd('/') + sp);
            foreach (string ap in new[] { "/api/auth/get-session", "/api/auth/session", "/auth/me", "/api/me", "/auth/session", "/api/user", "/me", "/user" })
                AddProbe(hostBase + ap);

            // Positive-only: a 2xx JSON session/user endpoint that names a user
            // (better-auth get-session, REST /me, …). A NON-2xx here proves
            // nothing — a bearer-token API 401s a cookie GET even when the cookie
            // login is fine — so it is never read as "not logged in".
            foreach (string url in probeUrls)
            {
                token.ThrowIfCancellationRequested();
                (int status, string body) = await ProbeAsync(http, url, token).ConfigureAwait(false);
                if (status is >= 200 and < 300 && LooksLikeUser(body)) return SessionVerdict.Authenticated;
            }

            // The one negative signal trusted to REJECT a login: an SPA that
            // server-renders its own logged-out state. Next.js/React sites emit
            // "isAuthenticated":false into the page from whatever session cookie
            // we send, so a login that came back with only a visitor cookie shows
            // it plainly (this is exactly the Philia "200 + anon cookie" trap).
            // Deliberately the sole rejecter — a missing/ambiguous signal returns
            // Unknown and is accepted, so no server-rendered-flag-less site that
            // logs in fine is ever turned away.
            (int rstatus, string rbody) = await ProbeAsync(http, "https://" + domain + "/", token).ConfigureAwait(false);
            if (rstatus is >= 200 and < 300)
            {
                // The flag is usually inside a Next.js RSC payload — a JSON string
                // embedded in self.__next_f.push([1,"…"]) — so every quote is
                // BACKSLASH-ESCAPED (\"isAuthenticated\":false). A search for the
                // plain "isAuthenticated":false silently misses it (this was a live
                // false-negative on Philia). Strip backslashes first so both the
                // escaped and the plain forms match, then read the boolean.
                string flat = rbody.Replace("\\", "");
                Match m = Regex.Match(flat, "\"isAuthenticated\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
                if (m.Success)
                    return m.Groups[1].Value.Equals("true", StringComparison.OrdinalIgnoreCase)
                        ? SessionVerdict.Authenticated : SessionVerdict.Unauthenticated;
            }

            return SessionVerdict.Unknown;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch { return SessionVerdict.Unknown; }   // never let verification break a login
    }

    private static async Task<(int status, string body)> ProbeAsync(HttpClient http, string url, CancellationToken token)
    {
        try
        {
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseContentRead, token).ConfigureAwait(false);
            // Bounded, but generous: a Next.js home page streams its RSC payload
            // in document order and the header's isAuthenticated flag lands in one
            // of the LAST chunks (measured ~282 KB into Philia's ~287 KB page), so
            // a tight cap silently truncates the very marker we're looking for.
            string body = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            if (body.Length > 1_000_000) body = body[..1_000_000];
            return ((int)resp.StatusCode, body);
        }
        catch { return (0, ""); }
    }

    /// <summary>A JSON body that carries a populated user identity.</summary>
    private static bool LooksLikeUser(string body)
    {
        string t = body.TrimStart();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return false;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("user", out JsonElement u))
                root = u;                                            // {session, user:{…}} shape
            if (root.ValueKind != JsonValueKind.Object) return false;
            foreach (string k in new[] { "id", "email", "username", "displayName", "name", "userId" })
                if (root.TryGetProperty(k, out JsonElement v) &&
                    v.ValueKind != JsonValueKind.Null &&
                    !(v.ValueKind == JsonValueKind.String && string.IsNullOrEmpty(v.GetString())))
                    return true;
        }
        catch { }
        return false;
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
