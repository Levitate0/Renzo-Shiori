using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace RenzoOAuthProxy.Services;

/// <summary>
/// Who may use this proxy, and which public base URLs it will build a
/// redirect_uri from.
///
/// The proxy listens on loopback, but the container runs with host networking
/// (so every process on the host can reach it) and the backend used to forward
/// <c>/oauth/*</c> to it from the internet. It accepted ANY non-empty
/// X-Instance-Key, which made <c>/refresh</c> an open "refresh any token with this
/// app's client secret" endpoint. Now the key must equal the one the backend
/// itself sends — resolved from the same places the backend resolves it
/// (ProxyScrobblerProvider.ResolveInstanceKey), since both processes share the
/// container's environment and data directory.
/// </summary>
public class InstanceGuardService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private readonly IConfiguration _configuration;
    private readonly ILogger<InstanceGuardService> _logger;
    private readonly object _lock = new();
    private string? _cachedKey;
    private HashSet<string> _cachedBases = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _cachedAt = DateTime.MinValue;

    public InstanceGuardService(IConfiguration configuration, ILogger<InstanceGuardService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// True when <paramref name="presented"/> is this instance's key. False —
    /// fail closed — when no key can be resolved at all.
    /// </summary>
    public bool IsValidInstanceKey([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? presented)
    {
        string? expected = Snapshot().key;
        if (string.IsNullOrEmpty(expected))
        {
            _logger.LogWarning("No instance key could be resolved; refusing proxy requests until the backend has written one.");
            return false;
        }
        return KeysEqual(presented, expected);
    }

    /// <summary>Constant-time comparison, so the key can't be recovered byte by byte from timings.</summary>
    public static bool KeysEqual(string? presented, string expected) =>
        !string.IsNullOrEmpty(presented)
        && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));

    /// <summary>
    /// The public base to build the redirect_uri from, or null when the caller's
    /// value is not allowed. With nothing configured to check against, the
    /// (already key-authenticated) caller's value is used as before.
    /// </summary>
    public string? ResolvePublicBase(string? requested, Func<string> requestOrigin)
    {
        string? configured = _configuration["OAuth:PublicBaseUrl"]?.Trim().TrimEnd('/');
        HashSet<string> allowed = Snapshot().bases;
        string? normalized = NormalizeBase(requested);
        if (requested == null)
            return string.IsNullOrEmpty(configured) ? requestOrigin() : configured;
        if (normalized == null)
            return null;
        if (allowed.Count == 0 || allowed.Contains(normalized))
            return normalized;
        return null;
    }

    /// <summary>scheme://authority of an absolute http(s) URL, or null.</summary>
    public static string? NormalizeBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || !string.IsNullOrEmpty(uri.UserInfo))
            return null;
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private (string? key, HashSet<string> bases) Snapshot()
    {
        lock (_lock)
        {
            if (DateTime.UtcNow - _cachedAt < CacheFor && _cachedKey != null)
                return (_cachedKey, _cachedBases);

            JsonElement? backend = ReadBackendSettings();
            // Same precedence as the backend: env overrides its appsettings.json,
            // Proxy:InstanceKey before the legacy InstanceKey, then the key file.
            string? key = FirstNonEmpty(
                _configuration["Scrobbling:Proxy:InstanceKey"],
                Get(backend, "Scrobbling", "Proxy", "InstanceKey"),
                _configuration["Scrobbling:InstanceKey"],
                Get(backend, "Scrobbling", "InstanceKey"),
                ReadKeyFile());

            var bases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Add(string? b) { if (NormalizeBase(b) is { } n) bases.Add(n); }
            Add(_configuration["OAuth:PublicBaseUrl"]);
            foreach (string b in _configuration.GetSection("OAuth:AllowedPublicBases").Get<string[]>() ?? [])
                Add(b);
            foreach (string b in (_configuration["OAuth:AllowedPublicBases"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                Add(b);
            Add(Get(backend, "FirstTimeSettings", "ExternalDomain"));
            if (backend is { } root && root.TryGetProperty("AllowedOrigins", out JsonElement origins) && origins.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement o in origins.EnumerateArray())
                    if (o.ValueKind == JsonValueKind.String) Add(o.GetString());
            }

            _cachedKey = key?.Trim();
            _cachedBases = bases;
            _cachedAt = DateTime.UtcNow;
            return (_cachedKey, _cachedBases);
        }
    }

    private string? DataDirectory()
    {
        string? dir = _configuration["OAuth:BackendDataDir"] ?? Environment.GetEnvironmentVariable("RENZO_DATADIR");
        if (!string.IsNullOrWhiteSpace(dir))
            return dir;
        return File.Exists("/config/appsettings.json") ? "/config" : null;
    }

    private JsonElement? ReadBackendSettings()
    {
        string? dir = DataDirectory();
        if (dir == null)
            return null;
        string path = Path.Combine(dir, "appsettings.json");
        try
        {
            if (!File.Exists(path))
                return null;
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not read the backend settings at {Path}: {Message}", path, ex.Message);
            return null;
        }
    }

    private string? ReadKeyFile()
    {
        string? dir = DataDirectory();
        if (dir == null)
            return null;
        string path = Path.Combine(dir, "scrobbler-instance.key");
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? Get(JsonElement? root, params string[] path)
    {
        if (root is not { } el)
            return null;
        foreach (string p in path)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(p, out el))
                return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
