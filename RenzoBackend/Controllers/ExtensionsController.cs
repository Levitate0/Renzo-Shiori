using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Providers;
using Microsoft.AspNetCore.Mvc;
using Mihon.ExtensionsBridge.Models;

namespace RenzoBackend.Controllers;

/// <summary>
/// Extension version management — the resilience layer for source extensions
/// that break on update: list installed versions, roll back to a known-good
/// one, pin it against auto-update, or sideload a patched APK.
/// </summary>
[ApiController]
[Route("api/extensions")]
public class ExtensionsController : ControllerBase
{
    private readonly MihonBridgeService _mihon;
    private readonly ProviderCacheService _providerCache;
    private readonly ILogger _logger;

    public ExtensionsController(MihonBridgeService mihon, ProviderCacheService providerCache, ILogger<ExtensionsController> logger)
    {
        _mihon = mihon;
        _providerCache = providerCache;
        _logger = logger;
    }

    private static object ToDto(RepositoryGroup g)
    {
        string active = (g.ActiveEntry >= 0 && g.ActiveEntry < g.Entries.Count)
            ? g.Entries[g.ActiveEntry].Extension.Version : "";
        return new
        {
            name = g.Name,
            autoUpdate = g.AutoUpdate,
            activeVersion = active,
            versions = g.Entries
                .OrderByDescending(e => e.Extension.VersionCode)
                .Select(e => new
                {
                    version = e.Extension.Version,
                    isLocal = e.IsLocal,
                    repositoryId = e.RepositoryId,
                }).ToList(),
        };
    }

    /// <summary>Installed extensions with every locally available version.</summary>
    [HttpGet]
    [RequireUserLevel(UserLevel.Manager)]
    public ActionResult ListExtensions()
    {
        var groups = _mihon.ListLocalExtensions()
            .OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
            .Select(ToDto)
            .ToList();
        return Ok(groups);
    }

    /// <summary>
    /// Switch the active version (rollback/forward). Selecting a non-latest
    /// version pins the extension so auto-update can't replace it.
    /// </summary>
    [HttpPost("active")]
    [RequireUserLevel(UserLevel.Manager)]
    public async Task<ActionResult> SetActiveAsync([FromQuery] string name, [FromQuery] string version, CancellationToken token = default)
    {
        try
        {
            RepositoryGroup g = await _mihon.SetExtensionVersionAsync(name, version, token).ConfigureAwait(false);
            _logger.LogInformation("Extension {Name} switched to version {Version} (autoUpdate={Auto})", name, version, g.AutoUpdate);
            return Ok(ToDto(g));
        }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to switch extension {Name} to {Version}", name, version);
            return StatusCode(500, new { error = "Could not switch the extension version." });
        }
    }

    /// <summary>Pin (autoupdate off) or unpin; unpinning re-activates the newest version.</summary>
    [HttpPost("autoupdate")]
    [RequireUserLevel(UserLevel.Manager)]
    public async Task<ActionResult> SetAutoUpdateAsync([FromQuery] string name, [FromQuery] bool enabled, CancellationToken token = default)
    {
        try
        {
            RepositoryGroup g = await _mihon.SetExtensionAutoUpdateAsync(name, enabled, token).ConfigureAwait(false);
            return Ok(ToDto(g));
        }
        catch (KeyNotFoundException e) { return NotFound(new { error = e.Message }); }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to set autoupdate for extension {Name}", name);
            return StatusCode(500, new { error = "Could not update the extension." });
        }
    }

    /// <summary>
    /// Sideload a patched/pinned APK. The build is compiled in a temp folder and
    /// only swapped in on success, then pinned against auto-update.
    /// </summary>
    [HttpPost("sideload")]
    [RequireUserLevel(UserLevel.Manager)]
    [RequestSizeLimit(100 * 1024 * 1024)]
    public async Task<ActionResult> SideloadAsync(IFormFile file, CancellationToken token = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No APK provided." });
        try
        {
            byte[] bytes;
            await using (Stream s = file.OpenReadStream())
            using (var ms = new MemoryStream())
            {
                await s.CopyToAsync(ms, token).ConfigureAwait(false);
                bytes = ms.ToArray();
            }
            RepositoryGroup? g = await _mihon.SideloadExtensionAsync(bytes, token).ConfigureAwait(false);
            if (g == null)
                return StatusCode(422, new { error = "The APK failed to compile — the previous version remains active." });
            // A newly sideloaded extension may introduce new sources.
            await _providerCache.RefreshCacheAsync(false, token).ConfigureAwait(false);
            _logger.LogInformation("Sideloaded extension {Name}, active version {Version} (pinned)", g.Name,
                g.Entries[g.ActiveEntry].Extension.Version);
            return Ok(ToDto(g));
        }
        catch (ArgumentException e) { return BadRequest(new { error = e.Message }); }
        catch (Exception e)
        {
            _logger.LogError(e, "Sideload failed for {FileName}", file.FileName);
            return StatusCode(500, new { error = "Sideload failed — the previous version remains active." });
        }
    }
}
