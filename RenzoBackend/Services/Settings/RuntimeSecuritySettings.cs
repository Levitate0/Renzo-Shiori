using RenzoBackend.Models.Dto;

namespace RenzoBackend.Services.Settings;

/// <summary>
/// Process-wide snapshot of the security settings that are consumed outside a
/// normal scoped-request flow (CORS policy resolution, JWT lifetimes). Seeded
/// from legacy appsettings.json keys at startup, then kept current by
/// SettingsService every time settings are loaded or saved — so edits made in
/// the WebUI take effect immediately, no restart or config-file editing needed.
/// </summary>
public static class RuntimeSecuritySettings
{
    private static volatile string[] _allowedOrigins = [];
    private static volatile int _sessionExpirationHours = 24;
    private static volatile int _rememberMeExpirationDays = 90;

    public static string[] AllowedOrigins => _allowedOrigins;
    public static int SessionExpirationHours => _sessionExpirationHours;
    public static int RememberMeExpirationDays => _rememberMeExpirationDays;

    public static void Update(EditableSettingsDto settings)
    {
        Set(settings.AllowedOrigins, settings.SessionExpirationHours, settings.RememberMeExpirationDays);
    }

    public static void Set(string[]? allowedOrigins, int sessionExpirationHours, int rememberMeExpirationDays)
    {
        // Normalize to the exact origin form CORS matching expects: scheme://host[:port],
        // no trailing slash, no whitespace. Drop entries that aren't parseable origins.
        var normalized = (allowedOrigins ?? [])
            .Select(o => o?.Trim().TrimEnd('/') ?? string.Empty)
            .Where(o => Uri.TryCreate(o, UriKind.Absolute, out var u)
                        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

#if DEBUG
        // Local dev serves the frontend from its own origin(s) and needs credentialed CORS.
        normalized = normalized
            .Concat(["http://localhost:5001", "http://localhost:3000"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
#endif

        _allowedOrigins = normalized;
        _sessionExpirationHours = sessionExpirationHours > 0 ? sessionExpirationHours : 24;
        _rememberMeExpirationDays = rememberMeExpirationDays > 0 ? rememberMeExpirationDays : 90;
    }
}
