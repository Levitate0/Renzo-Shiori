namespace RensaioBackend.Services.SiteAuth;

/// <summary>
/// How to log in to one site: its domain, login endpoint, and the field names
/// its form expects. The coin sites installed here are a CMS family that share
/// a login shape, so most use the same generic definition; per-site overrides
/// exist for the ones that differ. Cookie-only sites (CAPTCHA / social sign-in)
/// have no definition and use the paste-a-cookie path instead.
/// </summary>
public record SiteLoginDefinition(
    string Provider,
    string Domain,
    string LoginUrl,
    string UsernameField = "email",
    string PasswordField = "password",
    /// <summary>GET this first to pick up a CSRF/XSRF cookie+token, if the site uses one.</summary>
    string? CsrfPageUrl = null,
    string? CsrfField = null,
    /// <summary>Cookie name that proves a logged-in session (harvested + checked for expiry).</summary>
    string SessionCookieName = "");

public static class SiteLoginDefinitions
{
    // The ezmanga/philia/fairy/valir family run the same custom CMS: a Laravel-
    // style /auth/login POST with an XSRF-TOKEN cookie + token pair. Asura and
    // Utoon differ and are listed separately. These are best-effort defaults;
    // the real field names are confirmed the first time a user runs Test Login,
    // and any site can fall back to the paste-cookie path.
    private static readonly Dictionary<string, SiteLoginDefinition> _byProvider = new(StringComparer.OrdinalIgnoreCase)
    {
        ["EZmanga"] = new("EZmanga", "ezmanga.org", "https://ezmanga.org/auth/login",
            UsernameField: "email", PasswordField: "password",
            CsrfPageUrl: "https://ezmanga.org/", CsrfField: "_token",
            SessionCookieName: "laravel_session"),

        ["Philia Scans"] = new("Philia Scans", "philiascans.org", "https://philiascans.org/auth/login",
            CsrfPageUrl: "https://philiascans.org/", CsrfField: "_token",
            SessionCookieName: "laravel_session"),

        ["Fairy Scans"] = new("Fairy Scans", "fairyscans.org", "https://fairyscans.org/auth/login",
            CsrfPageUrl: "https://fairyscans.org/", CsrfField: "_token",
            SessionCookieName: "laravel_session"),

        ["Valir Scans"] = new("Valir Scans", "valirscans.org", "https://valirscans.org/auth/login",
            CsrfPageUrl: "https://valirscans.org/", CsrfField: "_token",
            SessionCookieName: "laravel_session"),

        ["Asura Scans"] = new("Asura Scans", "asurascans.com", "https://asurascans.com/api/auth/login",
            UsernameField: "email", PasswordField: "password",
            SessionCookieName: "session"),

        ["Utoon"] = new("Utoon", "utoon.net", "https://utoon.net/wp-login.php",
            UsernameField: "log", PasswordField: "pwd",
            SessionCookieName: "wordpress_logged_in"),
    };

    public static SiteLoginDefinition? Get(string provider) =>
        _byProvider.TryGetValue(provider, out var d) ? d : null;

    public static bool SupportsAutoLogin(string provider) => _byProvider.ContainsKey(provider);

    /// <summary>Providers we have a login definition for, for the UI's site picker.</summary>
    public static IReadOnlyCollection<SiteLoginDefinition> All => _byProvider.Values;

    /// <summary>Best-effort domain for a provider, even without a full definition.</summary>
    public static string? DomainFor(string provider) =>
        _byProvider.TryGetValue(provider, out var d) ? d.Domain : null;
}
