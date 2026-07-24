using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Providers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Controllers
{
    [ApiController]
    [Route("api/provider")]
    [Produces("application/json")]
    public class ProviderController : ControllerBase
    {
        private readonly ProviderManagerService _managerService;
        private readonly ProviderPreferencesService _preferencesService;
        private readonly ThumbCacheService _thumbs;
        private readonly AppDbContext _db;
        private readonly ILogger _logger;

        public ProviderController(
            ILogger<ProviderController> logger,
            ThumbCacheService thumbs,
            ProviderManagerService installationService,
            ProviderPreferencesService preferencesService,
            AppDbContext db)
        {
            _logger = logger;
            _thumbs = thumbs;
            _managerService = installationService;
            _preferencesService = preferencesService;
            _db = db;
        }

        private Guid CurrentUserId => (HttpContext.Items["User"] as UserEntity)?.Id ?? Guid.Empty;

        /// <summary>
        /// Sources the current user has personally enabled for Search/Browse/Add-series.
        /// Installing a source (any privilege level) doesn't make it visible to every
        /// user — each user opts in separately.
        /// </summary>
        [HttpGet("my-sources")]
        [ProducesResponseType(typeof(List<string>), 200)]
        public async Task<ActionResult<List<string>>> GetMySourcesAsync(CancellationToken token = default)
        {
            List<string> ids = await _db.UserProviders
                .Where(p => p.UserId == CurrentUserId)
                .Select(p => p.MihonProviderId)
                .ToListAsync(token).ConfigureAwait(false);
            return Ok(ids);
        }

        /// <summary>
        /// Every installed source (regardless of any user's personal enablement),
        /// each annotated with whether the CURRENT user has it enabled. Feeds the
        /// "My sources" toggle list — the only place a user can see (and opt into)
        /// sources they haven't enabled yet.
        /// </summary>
        [HttpGet("all-sources")]
        [ProducesResponseType(typeof(List<object>), 200)]
        public async Task<ActionResult> GetAllSourcesWithVisibilityAsync(
            [FromServices] Services.Search.SearchQueryService searchQuery, CancellationToken token = default)
        {
            var enabled = new HashSet<string>(
                await _db.UserProviders.Where(p => p.UserId == CurrentUserId).Select(p => p.MihonProviderId).ToListAsync(token).ConfigureAwait(false),
                StringComparer.Ordinal);
            var all = await searchQuery.GetAllInstalledSourcesAsync(token).ConfigureAwait(false);
            var result = all.Select(s => new
            {
                mihonProviderId = s.MihonProviderId,
                provider = s.Provider,
                scanlator = s.Scanlator,
                language = s.Language,
                enabled = enabled.Contains(s.MihonProviderId),
            }).OrderBy(s => s.provider).ThenBy(s => s.language).ToList();
            return Ok(result);
        }

        /// <summary>Enables an already-installed source for the current user only.</summary>
        [HttpPost("my-sources/{mihonProviderId}")]
        [ProducesResponseType(200)]
        public async Task<ActionResult> EnableMySourceAsync([FromRoute] string mihonProviderId, CancellationToken token = default)
        {
            Guid userId = CurrentUserId;
            bool exists = await _db.UserProviders.AnyAsync(p => p.UserId == userId && p.MihonProviderId == mihonProviderId, token).ConfigureAwait(false);
            if (!exists)
            {
                _db.UserProviders.Add(new UserProviderEntity { Id = Guid.NewGuid(), UserId = userId, MihonProviderId = mihonProviderId });
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            return Ok(new { success = true });
        }

        /// <summary>Disables a source for the current user only (doesn't affect other users or the underlying install).</summary>
        [HttpDelete("my-sources/{mihonProviderId}")]
        [ProducesResponseType(200)]
        public async Task<ActionResult> DisableMySourceAsync([FromRoute] string mihonProviderId, CancellationToken token = default)
        {
            Guid userId = CurrentUserId;
            UserProviderEntity? row = await _db.UserProviders.FirstOrDefaultAsync(p => p.UserId == userId && p.MihonProviderId == mihonProviderId, token).ConfigureAwait(false);
            if (row != null)
            {
                _db.UserProviders.Remove(row);
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            return Ok(new { success = true });
        }

        /// <summary>Auto-enables every MihonProviderId belonging to a just-installed package for the acting user, so they see it immediately without a second opt-in step.</summary>
        private Task EnableForInstallerAsync(string pkgName, CancellationToken token) => EnablePackageForUserAsync(pkgName, CurrentUserId, token);

        private async Task EnablePackageForUserAsync(string pkgName, Guid userId, CancellationToken token)
        {
            if (userId == Guid.Empty)
                return;
            List<string> providerIds = await _db.Providers
                .Where(p => p.SourcePackageName == pkgName)
                .Select(p => p.MihonProviderId)
                .ToListAsync(token).ConfigureAwait(false);
            if (providerIds.Count == 0)
                return;
            List<string> already = await _db.UserProviders
                .Where(p => p.UserId == userId && providerIds.Contains(p.MihonProviderId))
                .Select(p => p.MihonProviderId)
                .ToListAsync(token).ConfigureAwait(false);
            var alreadySet = new HashSet<string>(already, StringComparer.Ordinal);
            foreach (string id in providerIds)
            {
                if (alreadySet.Add(id))
                    _db.UserProviders.Add(new UserProviderEntity { Id = Guid.NewGuid(), UserId = userId, MihonProviderId = id });
            }
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
        }

        /// <summary>
        /// "Install" for a source that's already installed system-wide by someone
        /// else: cheaply adds every one of that package's sources to the CURRENT
        /// user's own enabled set, with no APK re-fetch/recompile. The UI shows the
        /// same "Install" action either way — this is the branch taken when
        /// <c>isInstaled</c> is already true but <c>isEnabledForMe</c> is false.
        /// </summary>
        [HttpPost("my-sources/package/{pkgName}")]
        [ProducesResponseType(200)]
        public async Task<ActionResult> EnablePackageForMeAsync([FromRoute] string pkgName, CancellationToken token = default)
        {
            await EnablePackageForUserAsync(pkgName, CurrentUserId, token).ConfigureAwait(false);
            return Ok(new { success = true });
        }

        /// <summary>Removes every one of a package's sources from the current user's own enabled set (doesn't touch the shared install or other users).</summary>
        [HttpDelete("my-sources/package/{pkgName}")]
        [ProducesResponseType(200)]
        public async Task<ActionResult> DisablePackageForMeAsync([FromRoute] string pkgName, CancellationToken token = default)
        {
            Guid userId = CurrentUserId;
            List<string> providerIds = await _db.Providers
                .Where(p => p.SourcePackageName == pkgName)
                .Select(p => p.MihonProviderId)
                .ToListAsync(token).ConfigureAwait(false);
            List<UserProviderEntity> rows = await _db.UserProviders
                .Where(p => p.UserId == userId && providerIds.Contains(p.MihonProviderId))
                .ToListAsync(token).ConfigureAwait(false);
            if (rows.Count > 0)
            {
                _db.UserProviders.RemoveRange(rows);
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            return Ok(new { success = true });
        }

        /// <summary>
        /// Gets a list of all available extensions (installed and available to install)
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of extensions</returns>
        /// <response code="200">Returns the list of extensions</response>
        /// <response code="500">If an error occurs while retrieving extensions</response>
        [HttpGet("list")]
        [ProducesResponseType(typeof(List<ExtensionDto>), 200)]
        [ProducesResponseType(typeof(object), 500)]
        public async Task<ActionResult<List<ExtensionDto>>> GetProvidersAsync(CancellationToken token = default)
        {
            try
            {
                var extensions = await _managerService.GetProvidersAsync(CurrentUserId, token).ConfigureAwait(false);
                await _thumbs.PopulateThumbsAsync(extensions, "/api/image/", token).ConfigureAwait(false);
                return Ok(extensions);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving extensions");
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Installs an extension by package name
        /// </summary>
        /// <param name="pkgName">Package name of the extension</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Success status</returns>
        /// <response code="200">Extension installed successfully</response>
        /// <response code="400">Failed to install extension</response>
        /// <response code="500">If an error occurs during installation</response>
        // Sources are installable by any user — the shared extension only ever
        // shows up in whoever installed it (or separately enabled it)'s own
        // Search/Browse, so there's no reason to gate the action itself.
        [HttpPost("install/{pkgName}")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(typeof(object), 500)]
        public async Task<IActionResult> InstallProvider([FromRoute] string pkgName, [FromQuery] string? repoName = null, [FromQuery] bool force = false, CancellationToken token = default)
        {
            try
            {
                var success = await _managerService.InstallProviderAsync(pkgName, repoName, force, token).ConfigureAwait(false);
                if (success)
                {
                    await EnableForInstallerAsync(pkgName, token).ConfigureAwait(false);
                    return Ok(new { message = "Extension installed successfully" });
                }
                return BadRequest(new { error = "Failed to install extension" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error installing extension {pkgName}", pkgName);
                return StatusCode(500, new { error = ex.Message });
            }
        }



        /// <summary>
        /// Gets the preferences for a provider extension
        /// </summary>
        /// <param name="pkgName">Package name of the extension</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Provider preferences</returns>
        /// <response code="200">Returns the provider preferences</response>
        /// <response code="400">Provider not found</response>
        /// <response code="500">If an error occurs while retrieving preferences</response>
        [HttpGet("preferences/{pkgName}")]
        [ProducesResponseType(typeof(ProviderPreferencesDto), 200)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(typeof(object), 500)]
        public async Task<ActionResult<ProviderPreferencesDto>> GetPreferencesAsync([FromRoute] string pkgName, CancellationToken token = default)
        {
            try
            {
                var prefs = await _preferencesService.GetProviderPreferencesAsync(pkgName, CurrentUserId, token).ConfigureAwait(false);
                if (prefs != null)
                {
                    return Ok(prefs);
                }
                return BadRequest(new { error = "Provider not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting preference of {pkgName}", pkgName);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Sets the preferences for a provider extension
        /// </summary>
        /// <param name="prefs">Provider preferences object</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Status of the operation</returns>
        /// <response code="200">Preferences set successfully</response>
        /// <response code="500">If an error occurs while setting preferences</response>
        // Any user can set their own preferences for a source they can see — this
        // now saves as THEIR value (see SetProviderPreferencesAsync), not a
        // system-wide change gated behind Manager.
        [HttpPost("preferences")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 500)]
        public async Task<IActionResult> SetPreferencesAsync([FromBody] ProviderPreferencesDto prefs, CancellationToken token = default)
        {
            try
            {
                await _preferencesService.SetProviderPreferencesAsync(prefs, CurrentUserId, token).ConfigureAwait(false);
                return Ok(new { message = "Preferences set successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting preferences for {PkgName}", prefs.PkgName);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Disables an extension by package name
        /// </summary>
        /// <param name="pkgName">Package name of the extension</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Success status</returns>
        /// <response code="200">Extension uninstalled successfully</response>
        /// <response code="400">Failed to uninstall extension</response>
        /// <response code="500">If an error occurs during uninstallation</response>
        [HttpPost("uninstall/{pkgName}")]
        [RequireUserLevel(UserLevel.Admin)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(typeof(object), 500)]
        public async Task<IActionResult> DisableProviderAsync([FromRoute] string pkgName, CancellationToken token = default)
        {
            try
            {
                var success = await _managerService.DisableProviderAsync(pkgName, token).ConfigureAwait(false);
                if (success)
                {
                    return Ok(new { message = "Extension disabled successfully" });
                }
                return BadRequest(new { error = "Failed to disabled extension" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error disabling extension {pkgName}", pkgName);
                return StatusCode(500, new { error = ex.Message });
            }
        }

        /// <summary>
        /// Installs an extension from an uploaded file
        /// </summary>
        /// <param name="file">The extension file to upload</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Success status</returns>
        /// <response code="200">Extension installed successfully</response>
        /// <response code="400">Failed to install extension</response>
        /// <response code="500">If an error occurs during installation</response>
        [HttpPost("install/file")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(typeof(object), 400)]
        [ProducesResponseType(typeof(object), 500)]
        public async Task<ActionResult<string>> InstallProviderFromFileAsync([FromForm] IFormFile file, [FromQuery] bool force = false, CancellationToken token = default)
        {
            if (file == null || file.Length == 0)
            {
                return BadRequest(new { error = "No file uploaded" });
            }

            try
            {
                using var ms = new MemoryStream();
                await file.CopyToAsync(ms, token).ConfigureAwait(false);
                var content = ms.ToArray();
                string? pkgName = await _managerService.InstallProviderFromFileAsync(content, force, token).ConfigureAwait(false);
                if (pkgName != null)
                {
                    await EnableForInstallerAsync(pkgName, token).ConfigureAwait(false);
                    return Ok(pkgName);
                }
                return BadRequest(new { error = "Failed to install extension" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error installing extension from file {FileName}", file?.FileName);
                return StatusCode(500, new { error =$"Error installing extension from file {file?.FileName ?? ""}."});
            }
        }
    }
}
