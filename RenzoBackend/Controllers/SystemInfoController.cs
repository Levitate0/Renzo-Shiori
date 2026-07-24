using System.Reflection;
using RenzoBackend.Services.Settings;
using Microsoft.AspNetCore.Mvc;

namespace RenzoBackend.Controllers;

/// <summary>
/// Unauthenticated server-discovery endpoint (Jellyfin-style /System/Info/Public).
/// Lets a client validate that an entered address is a live, compatible Renzo
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
            // Internal client-handshake identifier (NOT user-facing branding — the UI,
            // logos, and repo are all Renzo). Kept as "Rensaio" so previously-installed
            // desktop/APK builds, which probe this endpoint and match the literal
            // "Rensaio", still recognize this server after the rebrand. New Renzo clients
            // accept both names. `productName` carries the current brand for anything new.
            product = "Rensaio",
            productName = "Renzo",
            version,
            authenticationRequired = settings.AuthenticationEnabled
        });
    }
}
