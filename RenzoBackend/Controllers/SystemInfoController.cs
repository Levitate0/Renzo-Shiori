using System.Reflection;
using RenzoBackend.Services.Settings;
using RenzoBackend.Utils;
using Microsoft.AspNetCore.Mvc;

namespace RenzoBackend.Controllers;

/// <summary>
/// Unauthenticated server-discovery endpoint (Jellyfin-style /System/Info/Public).
/// Lets a client validate that an entered address is a live, compatible Renzo Shiori
/// server — and learn whether login is required — before showing a login screen.
/// Deliberately exposes nothing sensitive: no user data, no settings, no paths.
/// </summary>
[ApiController]
[Route("api/system")]
[Produces("application/json")]
public class SystemInfoController : ControllerBase
{
    private readonly SettingsService _settings;

    public SystemInfoController(SettingsService settings)
    {
        _settings = settings;
    }

    [HttpGet("info/public")]
    public async Task<ActionResult> GetPublicInfo(CancellationToken token = default)
    {
        var settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        return Ok(new
        {
            product = "Renzo Shiori",
            version,
            authenticationRequired = settings.AuthenticationEnabled
        });
    }

    /// <summary>
    /// Lightweight build fingerprint for the clients' silent auto-refresh poller.
    /// `build` combines the app version with the embedded frontend bundle hash, so
    /// it changes on every deploy that alters the UI. Clients poll this and reload
    /// when it changes (deferring while the reader is open). Never cached.
    /// </summary>
    [HttpGet("version")]
    public ActionResult GetVersion()
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        string hash = EnvironmentSetup.WwwRootHash;
        string build = string.IsNullOrEmpty(hash) ? version : $"{version}.{hash[..Math.Min(12, hash.Length)]}";
        Response.Headers["Cache-Control"] = "no-store, no-cache, must-revalidate";
        return Ok(new { version, build });
    }
}
