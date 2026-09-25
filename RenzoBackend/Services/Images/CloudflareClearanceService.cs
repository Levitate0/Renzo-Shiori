using System.Collections.Concurrent;
using System.Text.Json;
using RenzoBackend.Services.Settings;

namespace RenzoBackend.Services.Images
{
    /// <summary>A solved Cloudflare challenge: the clearance cookie and the UA it was issued to.</summary>
    public sealed record CloudflareClearance(string Cookie, string UserAgent);

    /// <summary>
    /// Obtains and caches Cloudflare clearance for an image host.
    ///
    /// Covers on a protected host (kagane.to, for one) answer 403 to a plain
    /// fetch — and to any User-Agent or Referer; nothing short of a real
    /// challenge solve gets through. The obvious move, handing the URL to the
    /// extension so its CloudflareInterceptor deals with it, does NOT work for
    /// images: a FlareSolverr-style solve returns the RENDERED PAGE, so an image
    /// request comes back as ~600 bytes of HTML rather than the file. That is why
    /// those covers never appeared no matter how often they were retried.
    ///
    /// What does work is the pattern both solvers are built for: solve ONCE to
    /// get `cf_clearance`, then make ordinary HTTP requests carrying that cookie
    /// and the matching User-Agent. Measured against a real cover: the solve
    /// takes ~8s, and the plain fetch that follows returns 562KB of webp in
    /// 0.35s. Every later cover on that host costs only the 0.35s.
    ///
    /// Clearance is per HOST and single-flighted. That matters more than the
    /// caching: a Browse grid asks for ~40 covers at once, and without the gate
    /// each one would start its own 8-second browser solve for the same host.
    /// </summary>
    public class CloudflareClearanceService
    {
        private sealed class Entry
        {
            public CloudflareClearance? Value;
            public DateTime ExpiresUtc;
            public DateTime RetryAfterUtc;
            public readonly SemaphoreSlim Gate = new(1, 1);
        }

        private readonly ConcurrentDictionary<string, Entry> _byHost = new(StringComparer.OrdinalIgnoreCase);
        private readonly IHttpClientFactory _factory;
        // SettingsService is SCOPED and this service is a singleton (clearance is
        // shared per host), so settings are read through a scope rather than by
        // capturing the instance — a captive dependency here fails DI validation
        // at startup, taking the whole app with it.
        private readonly IServiceScopeFactory _scopes;
        private readonly ILogger<CloudflareClearanceService> _logger;

        /// <summary>Cloudflare issues clearance for ~30 minutes; re-solve a little before that.</summary>
        private static readonly TimeSpan ClearanceLifetime = TimeSpan.FromMinutes(25);

        /// <summary>After a failed solve, leave the host alone rather than re-solving per image.</summary>
        private static readonly TimeSpan SolveFailureCooldown = TimeSpan.FromMinutes(10);

        public CloudflareClearanceService(IHttpClientFactory factory, IServiceScopeFactory scopes, ILogger<CloudflareClearanceService> logger)
        {
            _factory = factory;
            _scopes = scopes;
            _logger = logger;
        }

        /// <summary>
        /// Clearance for <paramref name="url"/>'s host, solving if needed. Null when
        /// the solver is disabled, unreachable, or recently failed for this host.
        /// </summary>
        public async Task<CloudflareClearance?> GetAsync(Uri url, CancellationToken token = default)
        {
            bool enabled;
            string solverUrl;
            TimeSpan solverTimeout;
            using (IServiceScope scope = _scopes.CreateScope())
            {
                var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var settings = await settingsService.GetSettingsAsync(token).ConfigureAwait(false);
                if (settings == null)
                    return null;
                enabled = settings.FlareSolverrEnabled;
                solverUrl = settings.FlareSolverrUrl;
                solverTimeout = settings.FlareSolverrTimeout;
            }
            if (!enabled || string.IsNullOrWhiteSpace(solverUrl))
                return null;

            Entry entry = _byHost.GetOrAdd(url.Host, _ => new Entry());

            if (entry.Value != null && entry.ExpiresUtc > DateTime.UtcNow)
                return entry.Value;

            await entry.Gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                // Another caller may have solved it while we queued — which is the
                // whole point of the gate.
                if (entry.Value != null && entry.ExpiresUtc > DateTime.UtcNow)
                    return entry.Value;
                if (entry.RetryAfterUtc > DateTime.UtcNow)
                    return null;

                CloudflareClearance? solved = await SolveAsync(solverUrl, url, solverTimeout, token).ConfigureAwait(false);
                if (solved == null)
                {
                    entry.RetryAfterUtc = DateTime.UtcNow.Add(SolveFailureCooldown);
                    return null;
                }

                entry.Value = solved;
                entry.ExpiresUtc = DateTime.UtcNow.Add(ClearanceLifetime);
                _logger.LogInformation("Obtained Cloudflare clearance for {Host}.", url.Host);
                return solved;
            }
            finally
            {
                entry.Gate.Release();
            }
        }

        /// <summary>Forget the clearance for a host, so the next request solves again.</summary>
        public void Invalidate(Uri url)
        {
            if (_byHost.TryGetValue(url.Host, out Entry? entry))
            {
                entry.Value = null;
                entry.ExpiresUtc = DateTime.MinValue;
            }
        }

        private async Task<CloudflareClearance?> SolveAsync(string solverUrl, Uri target, TimeSpan timeout, CancellationToken token)
        {
            try
            {
                // Both supported solvers speak the FlareSolverr v1 protocol.
                string endpoint = solverUrl.TrimEnd('/') + "/v1";
                int maxTimeoutMs = (int)Math.Clamp(timeout.TotalMilliseconds, 30_000, 180_000);

                var payload = JsonSerializer.Serialize(new
                {
                    cmd = "request.get",
                    url = target.ToString(),
                    maxTimeout = maxTimeoutMs,
                });

                HttpClient http = _factory.CreateClient(nameof(CloudflareClearanceService));
                // The browser solve is the slow part; allow for it plus overhead.
                http.Timeout = TimeSpan.FromMilliseconds(maxTimeoutMs + 30_000);

                using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await http.PostAsync(endpoint, content, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("Cloudflare solver answered {Status} for {Host}.", response.StatusCode, target.Host);
                    return null;
                }

                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                if (!doc.RootElement.TryGetProperty("solution", out JsonElement solution))
                    return null;

                string? userAgent = solution.TryGetProperty("userAgent", out JsonElement ua) ? ua.GetString() : null;
                if (!solution.TryGetProperty("cookies", out JsonElement cookies) || cookies.ValueKind != JsonValueKind.Array)
                    return null;

                foreach (JsonElement cookie in cookies.EnumerateArray())
                {
                    if (cookie.TryGetProperty("name", out JsonElement name) &&
                        string.Equals(name.GetString(), "cf_clearance", StringComparison.Ordinal) &&
                        cookie.TryGetProperty("value", out JsonElement value))
                    {
                        string? v = value.GetString();
                        if (!string.IsNullOrEmpty(v) && !string.IsNullOrEmpty(userAgent))
                            return new CloudflareClearance(v!, userAgent!);
                    }
                }

                // A host that is not actually challenged returns no cf_clearance;
                // nothing is wrong, there is simply nothing to carry.
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Cloudflare solve failed for {Host}.", target.Host);
                return null;
            }
        }
    }
}
