using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.SiteAuth;

namespace RenzoBackend.Services.Background;

/// <summary>
/// Keeps coin/paid-site logins alive.
///
/// Sessions on these sites expire on their own schedule, and nothing used to
/// notice: the cookies were harvested once when the credential was saved and
/// re-injected verbatim on restart, so when they lapsed the extension quietly
/// went back to serving only free chapters. There was no error — paid chapters
/// simply stopped appearing — which is indistinguishable from "the series has no
/// new chapters", the exact symptom that makes this class of bug so slow to spot.
///
/// So every credential with a stored password is re-authenticated periodically.
/// The site's own session lifetime is invisible to us, so this refreshes on a
/// fixed cadence well under a typical one rather than trying to predict expiry.
///
/// Failures back off (in memory — deliberately not a schema change): a wrong
/// password or a site that has changed its login form must not be retried every
/// tick forever. The backoff resets on success and on restart, so fixing the
/// credential in the UI takes effect immediately (that path logs in directly).
/// </summary>
public class SiteSessionRefreshService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SiteSessionRefreshService> _logger;

    /// <summary>How often to look for sessions due a refresh.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Re-login when the last successful one is older than this. Short enough to
    /// stay inside the session lifetime these sites typically issue, long enough
    /// that we're not hammering someone's login endpoint.
    /// </summary>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(8);

    /// <summary>Wait before the first pass so startup's own restore lands first.</summary>
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(3);

    private static readonly TimeSpan BackoffBase = TimeSpan.FromMinutes(30);
    private const int MaxBackoffSteps = 5;      // caps at 2.5h between attempts

    private readonly Dictionary<Guid, (int failures, DateTime nextAttempt)> _backoff = new();

    public SiteSessionRefreshService(IServiceScopeFactory scopeFactory, ILogger<SiteSessionRefreshService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshDueAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Never let a bad pass kill the loop — the next tick retries.
                _logger.LogWarning(ex, "Site session refresh pass failed");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task RefreshDueAsync(CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SiteAuthService auth = scope.ServiceProvider.GetRequiredService<SiteAuthService>();

        List<SiteCredentialEntity> creds = await db.SiteCredentials.ToListAsync(token).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;
        int refreshed = 0;

        foreach (SiteCredentialEntity cred in creds)
        {
            token.ThrowIfCancellationRequested();

            // Cookie-only credentials have nothing to re-authenticate with; a
            // re-login attempt would just overwrite a working manual cookie with
            // a "needs_login" status.
            if (string.IsNullOrEmpty(cred.EncryptedPassword))
                continue;

            if (_backoff.TryGetValue(cred.Id, out var state) && now < state.nextAttempt)
                continue;

            // Anything not currently OK is due immediately — that's a session we
            // already know isn't working.
            bool stale = cred.LastLoginAt == null || now - cred.LastLoginAt.Value >= RefreshAfter;
            bool broken = !string.Equals(cred.Status, "ok", StringComparison.OrdinalIgnoreCase);
            if (!stale && !broken)
                continue;

            SiteLoginResult result;
            try
            {
                result = await auth.LoginAsync(cred, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Site session refresh threw for {Provider}", cred.Provider);
                Penalize(cred.Id, now);
                continue;
            }

            if (result.Success)
            {
                _backoff.Remove(cred.Id);
                refreshed++;
                _logger.LogInformation("Refreshed site session for {Provider} ({Cookies} cookies).",
                    cred.Provider, result.CookiesInjected);
            }
            else
            {
                Penalize(cred.Id, now);
                _logger.LogWarning("Could not refresh site session for {Provider}: {Detail}",
                    cred.Provider, result.Detail);
            }
        }

        // LoginAsync mutates the entity (cookies, LastLoginAt, status) whether it
        // succeeded or not, and a recorded failure is worth keeping too — the UI
        // badge is how the user finds out a login needs attention. EF no-ops when
        // nothing actually changed.
        await db.SaveChangesAsync(token).ConfigureAwait(false);
        if (refreshed > 0)
            _logger.LogInformation("Site session refresh: {Count} session(s) renewed.", refreshed);
    }

    private void Penalize(Guid id, DateTime now)
    {
        int failures = _backoff.TryGetValue(id, out var s) ? s.failures + 1 : 1;
        int steps = Math.Min(failures, MaxBackoffSteps);
        _backoff[id] = (failures, now + BackoffBase * steps);
    }
}
