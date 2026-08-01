using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Jobs;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.Series;
using RenzoBackend.Services.Settings;
using RenzoBackend.Services.Status;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Controllers
{
    [ApiController]
    [Route("api/serie")]
    public class SeriesController : ControllerBase
    {
        private readonly ILogger _logger;
        private readonly SeriesQueryService _queryService;
        private readonly SeriesCommandService _commandService;
        private readonly SeriesProviderService _providerService;
        private readonly SeriesArchiveService _archiveService;
        private readonly ThumbCacheService _thumb;
        private readonly JobManagementService _jobManagementService;
        private readonly AppDbContext _db;
        private readonly StatusEvaluationService _statusEvaluation;
        private readonly SettingsService _settings;
        private readonly SeriesRelocationService _relocation;
        private readonly IServiceScopeFactory _scopeFactory;

        public SeriesController(ILogger<SeriesController> logger,
            SeriesQueryService queryService,
            SeriesCommandService commandService,
            SeriesProviderService providerService,
            SeriesArchiveService archiveService,
            ThumbCacheService thumbCacheService,
            JobManagementService jobManagementService,
            AppDbContext db,
            StatusEvaluationService statusEvaluation,
            SettingsService settings,
            SeriesRelocationService relocation,
            IServiceScopeFactory scopeFactory)
        {
            _logger = logger;
            _queryService = queryService;
            _commandService = commandService;
            _providerService = providerService;
            _archiveService = archiveService;
            _thumb = thumbCacheService;
            _jobManagementService = jobManagementService;
            _db = db;
            _statusEvaluation = statusEvaluation;
            _settings = settings;
            _relocation = relocation;
            _scopeFactory = scopeFactory;
        }

        // Guards a single in-flight auto-categorization run (it's long: one MangaDex
        // lookup per series). A second trigger while one is running is a no-op.
        private static int _recategorizeRunning;

        // ── Per-user library isolation ──────────────────────────────────────
        // Each series belongs to one owner (SeriesEntity.OwnerId); users only see
        // and manage their own library. Owner-level accounts may pass
        // ?viewAll=true to see across every user's library (support/troubleshooting).
        private UserEntity? CurrentUser => HttpContext.Items["User"] as UserEntity;
        private Guid CurrentUserId => CurrentUser?.Id ?? Guid.Empty;
        private bool IsOwnerLevel => CurrentUser?.Level == UserLevel.Owner;

        /// <summary>Resolves the effective "view all libraries" flag: only ever true for an Owner-level requester who asked for it.</summary>
        private bool ResolveAllowAll(bool viewAll) => viewAll && IsOwnerLevel;

        /// <summary>
        /// Loads a series' owner and returns a 404/403 ActionResult if the current
        /// user may not access it, or null if access is allowed. Call at the top of
        /// every single-series endpoint: <c>if (await DenyAccessAsync(id, token) is { } deny) return deny;</c>
        /// </summary>
        private async Task<ActionResult?> DenyAccessAsync(Guid seriesId, CancellationToken token)
        {
            Guid? ownerId = await _db.Series.Where(s => s.Id == seriesId).Select(s => (Guid?)s.OwnerId)
                .FirstOrDefaultAsync(token).ConfigureAwait(false);
            if (ownerId == null)
                return NotFound(new { success = false, error = "Series not found" });
            if (!SeriesQueryService.CanAccessSeries(ownerId.Value, CurrentUserId, IsOwnerLevel))
                return StatusCode(403, new { success = false, error = "This series belongs to another user's library." });
            return null;
        }

        /// <summary>
        /// Gets detailed information about a series by its unique identifier.
        /// </summary>
        /// <param name="id">The unique identifier of the series.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Extended information about the series.</returns>
        [HttpGet]
        [ProducesResponseType(typeof(SeriesExtendedDto), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<SeriesExtendedDto>> GetSeriesAsync([FromQuery] Guid id, CancellationToken token = default)
        {
            try
            {
                var result = await _queryService.GetSeriesAsync(id, CurrentUserId, IsOwnerLevel, token).ConfigureAwait(false);
                if (result == null)
                    return NotFound(new { success = false, error = "Series not found" });
                await _thumb.PopulateThumbsAsync(result.Providers,"/api/image/", token).ConfigureAwait(false);
                await _thumb.PopulateThumbsAsync(result, "/api/image/", token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting series: {Message}", ex.Message);
                return StatusCode(500, $"Error getting series.");
            }
        }

        [HttpGet("verify")]
        [ProducesResponseType(typeof(SeriesIntegrityResultDto), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<SeriesIntegrityResultDto>> VerifyIntegrityAsync([FromQuery] Guid g, [FromQuery] bool force = false, CancellationToken token = default)
        {
            try
            {
                if (await DenyAccessAsync(g, token).ConfigureAwait(false) is { } deny) return deny;
                var result = await _archiveService.VerifyIntegrityAsync(g, force, token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error verifying integrity: {Message}", ex.Message);
                return StatusCode(500, $"Error verifying integrity.");
            }
        }

        [HttpGet("cleanup")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> CleanupSeriesAsync([FromQuery] Guid g, CancellationToken token = default)
        {
            try
            {
                if (await DenyAccessAsync(g, token).ConfigureAwait(false) is { } deny) return deny;
                await _archiveService.CleanupSeriesAsync(g, token).ConfigureAwait(false);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error cleanup series: {Message}", ex.Message);
                return StatusCode(500, $"Error cleanup series.");
            }
        }

        [HttpPost("update-all")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult> UpdateAllSeriesAsync(CancellationToken token = default)
        {
            try
            {
                await _jobManagementService.EnqueueJobAsync(JobType.UpdateAllSeries, (string?)null, Priority.High, null, null, null, "Default", token).ConfigureAwait(false);
                return Ok(new { success = true, message = "Update All Series Queued" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error Updating All Series");
                return StatusCode(500, new { error = $"An error occurred during Error Updating All Series: {ex.Message}" });
            }
        }

        /// <summary>
        /// Triggers an immediate metadata + new-chapter refresh for a single series.
        /// Re-fetches status, title, cover and description from each active provider.
        /// Paused series refresh metadata but do not download.
        /// </summary>
        /// <param name="id">The unique identifier of the series.</param>
        /// <param name="token">Cancellation token.</param>
        [HttpPost("refresh")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> RefreshSeriesAsync([FromQuery] Guid id, [FromQuery] bool ifStale = false, CancellationToken token = default)
        {
            try
            {
                if (id == Guid.Empty)
                    return BadRequest("No series id provided");
                if (await DenyAccessAsync(id, token).ConfigureAwait(false) is { } deny) return deny;
                // ifStale: the open-a-series auto-refresh — only re-fetch providers
                // not successfully scanned in the last 15 minutes, so browsing the
                // library doesn't hammer sources.
                int queued = await _commandService.RefreshSeriesMetadataAsync(id,
                    ifStale ? TimeSpan.FromMinutes(15) : null, token).ConfigureAwait(false);
                return Ok(new { success = true, queued });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error refreshing series: {Message}", ex.Message);
                return StatusCode(500, "Error refreshing series.");
            }
        }

        /// <summary>
        /// Manually scans a SINGLE series for new chapters now — enqueues an immediate GetChapters
        /// check for every active provider of the series, ignoring the recurring schedule and the
        /// open-a-series staleness guard. The per-provider queue key dedupes against an in-flight scan.
        /// </summary>
        /// <param name="id">The unique identifier of the series to scan.</param>
        /// <param name="token">Cancellation token.</param>
        [HttpPost("scan")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> ScanSeriesAsync([FromQuery] Guid id, CancellationToken token = default)
        {
            try
            {
                if (id == Guid.Empty)
                    return BadRequest("No series id provided");
                if (await DenyAccessAsync(id, token).ConfigureAwait(false) is { } deny) return deny;
                // Explicit user action, so also prune stale "missing" chapters left
                // behind by a source that's since been disabled — see the method
                // doc for why this isn't in the passive auto-refresh path too.
                int pruned = await _commandService.CleanupDisabledSourceChaptersAsync(id, token).ConfigureAwait(false);
                int queued = await _commandService.RefreshSeriesMetadataAsync(id, null, token).ConfigureAwait(false);
                return Ok(new { success = true, queued, pruned });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error scanning series: {Message}", ex.Message);
                return StatusCode(500, "Error scanning series.");
            }
        }

        /// <summary>
        /// Live progress of the new-chapter scan: how many per-provider chapter
        /// checks are still waiting/running in the queue. Drives the scan
        /// progress bar on the Updates page.
        /// </summary>
        [HttpGet("scan-status")]
        [ProducesResponseType(typeof(object), 200)]
        public async Task<ActionResult> GetScanStatusAsync(CancellationToken token = default)
        {
            int waiting = await _jobManagementService.QueuedJobs
                .CountAsync(q => q.JobType == JobType.GetChapters && q.Status == QueueStatus.Waiting, token).ConfigureAwait(false);
            int running = await _jobManagementService.QueuedJobs
                .CountAsync(q => q.JobType == JobType.GetChapters && q.Status == QueueStatus.Running, token).ConfigureAwait(false);
            return Ok(new { waiting, running });
        }

        /// <summary>
        /// "Update now": queues an immediate library-wide new-chapter scan (a
        /// GetChapters check for every active provider of every series).
        /// </summary>
        [HttpPost("scan-all")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        public async Task<ActionResult> ScanAllSeriesAsync(CancellationToken token = default)
        {
            try
            {
                await _jobManagementService.EnqueueJobAsync(JobType.LibraryScan, (string?)null, Priority.High,
                    "LibraryScan", null, null, "Default", token).ConfigureAwait(false);
                return Ok(new { success = true, message = "Library scan queued" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queueing library scan");
                return StatusCode(500, new { error = "Could not queue the library scan." });
            }
        }

        /// <summary>
        /// Queues a download of every not-yet-downloaded chapter for a series (across all active
        /// sources). Already-downloaded chapters are left untouched. Blocked while the series is paused.
        /// </summary>
        /// <param name="seriesId">The unique identifier of the series.</param>
        /// <param name="token">Cancellation token.</param>
        [HttpPost("download-all")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(409)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> DownloadAllAsync([FromQuery] Guid seriesId, CancellationToken token = default)
        {
            try
            {
                if (seriesId == Guid.Empty)
                    return BadRequest("No series id provided");
                if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
                int queued = await _commandService.QueueDownloadAllAsync(seriesId, token).ConfigureAwait(false);
                return queued switch
                {
                    -2 => NotFound(new { success = false, error = "Series not found" }),
                    -1 => StatusCode(409, new { success = false, error = "Series is paused" }),
                    _ => Ok(new { success = true, queued }),
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error queuing download-all: {Message}", ex.Message);
                return StatusCode(500, "Error queuing downloads.");
            }
        }

        /// <summary>
        /// Deletes downloaded chapter files for a series. With no body (or an empty
        /// chapter list) every downloaded chapter is removed; otherwise only the
        /// listed chapter numbers. Metadata stays so chapters can be re-downloaded.
        /// </summary>
        /// <param name="seriesId">The unique identifier of the series.</param>
        /// <param name="request">Optional list of chapter numbers to delete; null/empty = all.</param>
        /// <param name="token">Cancellation token.</param>
        [HttpPost("delete-downloads")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> DeleteDownloadsAsync([FromQuery] Guid seriesId,
            [FromBody] DeleteDownloadsRequest? request = null, CancellationToken token = default)
        {
            try
            {
                if (seriesId == Guid.Empty)
                    return BadRequest("No series id provided");
                if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
                int deleted = await _commandService.DeleteDownloadsAsync(seriesId, request?.ChapterNumbers, token).ConfigureAwait(false);
                if (deleted == -2)
                    return NotFound(new { success = false, error = "Series not found" });
                return Ok(new { success = true, deleted });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting downloads: {Message}", ex.Message);
                return StatusCode(500, "Error deleting downloads.");
            }
        }

        /// <summary>
        /// Gets the unified, series-level chapter list (merged across every source). Each chapter
        /// reports whether it is downloaded and from which source, versus genuinely missing, plus the
        /// sources available for (re-)download.
        /// </summary>
        /// <param name="seriesId">The unique identifier of the series.</param>
        /// <param name="token">Cancellation token.</param>
        [HttpGet("chapters")]
        [ProducesResponseType(typeof(List<ChapterDetailDto>), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<ChapterDetailDto>>> GetSeriesChaptersAsync([FromQuery] Guid seriesId, CancellationToken token = default)
        {
            try
            {
                if (seriesId == Guid.Empty)
                    return BadRequest("No series id provided");
                var result = await _queryService.GetSeriesChaptersAsync(seriesId, CurrentUserId, IsOwnerLevel, token).ConfigureAwait(false);
                if (result == null)
                    return NotFound(new { success = false, error = "Series not found" });
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting series chapters: {Message}", ex.Message);
                return StatusCode(500, "Error getting series chapters.");
            }
        }

        /// <summary>
        /// Re-downloads (or downloads) a single chapter, replacing any existing file. The source is
        /// resolved by priority (storage → current holder → any available) unless an explicit
        /// <paramref name="providerId"/> override is supplied. Blocked while the series is paused.
        /// </summary>
        /// <param name="seriesId">The series owning the chapter.</param>
        /// <param name="chapter">The chapter number to (re-)download.</param>
        /// <param name="providerId">Optional source to force; omit for the priority default.</param>
        /// <param name="token">Cancellation token.</param>
        [HttpPost("chapter/redownload")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(409)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> RedownloadChapterAsync([FromQuery] Guid seriesId, [FromQuery] decimal chapter, [FromQuery] Guid? providerId = null, CancellationToken token = default)
        {
            try
            {
                if (seriesId == Guid.Empty)
                    return BadRequest("No series id provided");
                if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;

                RedownloadResult result = await _commandService.RedownloadChapterAsync(seriesId, chapter, providerId, token).ConfigureAwait(false);
                return result.Outcome switch
                {
                    RedownloadOutcome.Queued => Ok(new { success = true, queued = result.Queued, sourceProviderName = result.SourceProviderName }),
                    RedownloadOutcome.Paused => StatusCode(409, new { success = false, error = "Series is paused" }),
                    RedownloadOutcome.SeriesNotFound => NotFound(new { success = false, error = "Series not found" }),
                    RedownloadOutcome.ChapterNotFound => NotFound(new { success = false, error = "Chapter not found at source" }),
                    RedownloadOutcome.NoSourceAvailable => BadRequest(new { success = false, error = "No source available to download this chapter" }),
                    _ => StatusCode(500, new { success = false, error = "Error re-downloading chapter" })
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error re-downloading chapter: {Message}", ex.Message);
                return StatusCode(500, "Error re-downloading chapter.");
            }
        }

        /// <summary>
        /// POST /api/serie/apply-default-priority — the Sources page's "Default
        /// priority order" tab's "Apply to All" action. Re-ranks every series the
        /// CALLER owns (plus adopts any ownerless legacy series) to match their
        /// configured default order, and turns on the per-user redownload-on-
        /// upgrade setting. No-ops (returns applied=false) if the caller hasn't
        /// configured a default order yet.
        /// </summary>
        [HttpPost("apply-default-priority")]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(401)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> ApplyDefaultPriorityToAllAsync(CancellationToken token)
        {
            try
            {
                if (CurrentUserId == Guid.Empty)
                    return StatusCode(401, new { success = false, error = "Not signed in" });

                ApplyPriorityToAllResult result = await _commandService
                    .ApplyDefaultPriorityToAllSeriesAsync(CurrentUserId, token).ConfigureAwait(false);
                return Ok(new
                {
                    success = result.Applied,
                    error = result.Applied ? null : "No default priority order configured yet.",
                    seriesConsidered = result.SeriesConsidered,
                    seriesReordered = result.SeriesReordered,
                    seriesAdopted = result.SeriesAdopted,
                    chaptersQueued = result.ChaptersQueued,
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error applying default priority order to all series: {Message}", ex.Message);
                return StatusCode(500, "Error applying default priority order.");
            }
        }

        /// <summary>
        /// PUT /api/serie/{id}/category — move a series into a different category
        /// folder (Manga / Manhwa / Manhua / …). Physically relocates the folder,
        /// updates the stored path, and rewrites renzo.json to match. A null/empty
        /// category un-categorizes (moves it back to the library root).
        /// </summary>
        [HttpPut("{id:guid}/category")]
        public async Task<ActionResult> SetCategoryAsync(Guid id, [FromBody] SetSeriesCategoryRequest request, CancellationToken token)
        {
            if (await DenyAccessAsync(id, token).ConfigureAwait(false) is { } deny) return deny;

            var result = await _relocation.RelocateToCategoryAsync(id, request?.Category, token).ConfigureAwait(false);
            _logger.LogInformation("SetCategory {Id} -> '{Cat}': moved={Moved} path={Path} detail={Detail}",
                id, request?.Category, result.Moved, result.StoragePath, result.Reason);
            return Ok(new { success = true, moved = result.Moved, storagePath = result.StoragePath, detail = result.Reason });
        }

        public sealed class SetSeriesCategoryRequest
        {
            public string? Category { get; set; }
        }

        /// <summary>
        /// POST /api/serie/recategorize — auto-categorize the whole library into
        /// Manga/Manhwa/Manhua/… using MangaDex country-of-origin + format tags,
        /// relocating only series it can confidently place elsewhere. Owner only.
        /// Runs in the background (one MangaDex lookup per series); watch the logs.
        /// Pass ?dryRun=true to log what it WOULD move without touching anything.
        /// </summary>
        [HttpPost("recategorize")]
        [RequireUserLevel(UserLevel.Owner)]
        public ActionResult Recategorize([FromQuery] bool dryRun = false)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _recategorizeRunning, 1, 0) != 0)
                return StatusCode(409, new { success = false, error = "Auto-categorization is already running." });

            _ = Task.Run(async () =>
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var svc = scope.ServiceProvider.GetRequiredService<CategoryMaintenanceService>();
                    await svc.RecategorizeAllAsync(null, dryRun, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Auto-categorization run failed");
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _recategorizeRunning, 0);
                }
            });

            return Accepted(new { success = true, dryRun, message = "Auto-categorization started — check server logs for progress." });
        }

        /// <summary>
        /// Gets the user's library of series.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>List of series in the library.</returns>
        [HttpGet("library")]
        [ProducesResponseType(typeof(List<SeriesInfoDto>), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<SeriesInfoDto>>> GetLibraryAsync([FromQuery] bool viewAll = false, CancellationToken token = default)
        {
            try
            {
                var result = await _queryService.GetLibraryAsync(CurrentUserId, ResolveAllowAll(viewAll), token).ConfigureAwait(false);
                await _thumb.PopulateThumbsAsync(result, "/api/image/", token).ConfigureAwait(false);
                await _thumb.PopulateThumbsAsync(result.SelectMany(a=>a.Providers).Where(a=>a!=null), "/api/image/", token).ConfigureAwait(false);
                await _thumb.PopulateThumbsAsync(result.Where(a=>a.LastChangeProvider!=null).Select(a=>a.LastChangeProvider), "/api/image/", token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting library: {Message}", ex.Message);
                return StatusCode(500, $"Error getting library.");
            }
        }

        /// <summary>
        /// Gets the "Updates" feed: recently downloaded chapters and recently
        /// added series, newest first.
        /// </summary>
        /// <param name="start">Starting index for pagination.</param>
        /// <param name="count">Number of items to return.</param>
        /// <param name="token">Cancellation token.</param>
        [HttpGet("updates")]
        [ProducesResponseType(typeof(List<UpdateFeedItemDto>), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<UpdateFeedItemDto>>> GetUpdatesAsync([FromQuery] int start = 0, [FromQuery] int count = 100, [FromQuery] bool viewAll = false, CancellationToken token = default)
        {
            try
            {
                var result = await _queryService.GetUpdatesFeedAsync(start, count, CurrentUserId, ResolveAllowAll(viewAll), token).ConfigureAwait(false);
                await _thumb.PopulateThumbsAsync(result, "/api/image/", token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting updates feed: {Message}", ex.Message);
                return StatusCode(500, $"Error getting updates feed.");
            }
        }

        [HttpGet("latest")]
        [ProducesResponseType(typeof(List<LatestSeriesDto>), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<LatestSeriesDto>>> GetLatestAsync([FromQuery] int start, [FromQuery] int count, [FromQuery] string? sourceId = null, [FromQuery] string? keyword = null, [FromQuery(Name = "genre")] string[]? genre = null, [FromQuery] bool viewAll = false, CancellationToken token = default)
        {
            try
            {
                var result = await _queryService.GetLatestAsync(start, count, sourceId, keyword, genre, CurrentUserId, ResolveAllowAll(viewAll), token).ConfigureAwait(false);
                await _thumb.PopulateThumbsAsync(result, "/api/image/", token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting latest cloud library: {Message}", ex.Message);
                return StatusCode(500, $"Error getting latest cloud library.");
            }
        }

        /// <summary>
        /// Gets the distinct tags/genres present in the cached "Latest" cloud catalogue,
        /// each with the number of series carrying it. Populates the browse-screen tag filter.
        /// </summary>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Distinct genres with their occurrence counts.</returns>
        [HttpGet("latest/genres")]
        [ProducesResponseType(typeof(List<LatestGenreDto>), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<List<LatestGenreDto>>> GetLatestGenresAsync(CancellationToken token = default)
        {
            try
            {
                var result = await _queryService.GetLatestGenresAsync(token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting latest genres: {Message}", ex.Message);
                return StatusCode(500, $"Error getting latest genres.");
            }
        }

        /// <summary>
        /// Gets a provider match by provider ID.
        /// </summary>
        /// <param name="providerId">The provider's unique identifier.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The provider match if found.</returns>
        [HttpGet("match/{providerId}")]
        [ProducesResponseType(typeof(ProviderMatchDto), 200)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<ProviderMatchDto?>> GetMatchAsync([FromRoute] Guid providerId, CancellationToken token = default)
        {
            try
            {
                var result = await _providerService.GetMatchAsync(providerId, token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting provider match: {Message}", ex.Message);
                return StatusCode(500, $"Error getting provider match.");
            }
        }

        /// <summary>
        /// Sets a provider match.
        /// </summary>
        /// <param name="pmatch">The provider match object.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>True if the match was set successfully.</returns>
        [HttpPost("match")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(bool), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<bool>> SetMatchAsync([FromBody] ProviderMatchDto pmatch, CancellationToken token = default)
        {
            try
            {
                if (pmatch == null)
                    return BadRequest("No provider match provided");
                var result = await _providerService.SetMatchAsync(pmatch, token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting provider match: {Message}", ex.Message);
                return StatusCode(500, $"Error setting provider match.");
            }
        }

        /// <summary>
        /// Add a series with full details directly to the database.
        /// </summary>
        /// <param name="series">List of full series with complete information to add.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The ID of the newly created series.</returns>
        [HttpPost]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> AddSeriesAsync([FromBody] AugmentedResponseDto series, CancellationToken token = default)
        {
            try
            {
                if (series == null || series.Series == null || series.Series.Count == 0)
                {
                    return BadRequest("No series provided to add");
                }

                var seriesId = await _commandService.AddSeriesAsync(series, CurrentUserId, token).ConfigureAwait(false);

                // Import Series Wizard: sync ExternalMappings from renzo.json into SeriesMappings
                // with the logged-in user's level for role-based overwrite protection
                if (HttpContext.Items["User"] is UserEntity user &&
                    series.LocalInfo?.Series.ExternalMappings?.Count > 0)
                {
                    await _commandService.SyncExternalMappingsFromSnapshotAsync(
                        seriesId, series.LocalInfo, user.Id, user.Level, token).ConfigureAwait(false);
                }

                return Ok(new { id = seriesId });
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(403, new { success = false, error = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error adding full series: {Message}", ex.Message);
                return StatusCode(500, $"Error adding full series.");
            }
        }

        /// <summary>
        /// Update a series with full details directly to the database.
        /// </summary>
        /// <param name="series">Series with complete information to update.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The updated series information.</returns>
        [HttpPatch]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<SeriesExtendedDto>> UpdateSeriesAsync([FromBody] SeriesExtendedDto series, CancellationToken token = default)
        {
            try
            {
                if (series == null)
                {
                    return BadRequest("No series provided to update");
                }
                if (await DenyAccessAsync(series.Id, token).ConfigureAwait(false) is { } deny) return deny;

                series = await _commandService.UpdateSeriesAsync(series, token).ConfigureAwait(false);
                await _thumb.PopulateThumbsAsync(series, "/api/image/", token).ConfigureAwait(false);
                return Ok(series);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating series: {Message}", ex.Message);
                return StatusCode(500, $"Error updating series.");
            }
        }

        [HttpDelete]
        [RequireUserLevel(UserLevel.Admin)]
        [ProducesResponseType(typeof(object), 200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(500)]
        public async Task<ActionResult> DeleteSeriesAsync([FromQuery] Guid id, [FromQuery] bool alsoPhysical = false, CancellationToken token = default)
        {
            try
            {
                if (await DenyAccessAsync(id, token).ConfigureAwait(false) is { } deny) return deny;
                await _commandService.DeleteSeriesAsync(id, alsoPhysical, token).ConfigureAwait(false);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting series: {Message}", ex.Message);
                return StatusCode(500, $"Error updating series with id {id}");
            }
        }
        /// <summary>
        /// Sets the release cadence for a series (user override).
        /// Stores as a negative value to indicate user-set, preventing auto-recalculation.
        /// Re-evaluates health alerts after updating the cadence.
        /// </summary>
        [HttpPatch("{id}/cadence")]
        [RequireUserLevel(UserLevel.Manager)]
        [ProducesResponseType(200)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> SetSeriesCadenceAsync(Guid id, [FromBody] SetCadenceRequest request, CancellationToken token = default)
        {
            try
            {
                var series = await _db.Series.FirstOrDefaultAsync(s => s.Id == id, token).ConfigureAwait(false);
                if (series == null)
                    return NotFound(new { error = "Series not found" });
                if (!SeriesQueryService.CanAccessSeries(series.OwnerId, CurrentUserId, IsOwnerLevel))
                    return StatusCode(403, new { error = "This series belongs to another user's library." });

                if (request.CadenceDays.HasValue)
                {
                    if (request.CadenceDays.Value <= 0)
                        return BadRequest(new { error = "Cadence must be greater than zero" });

                    // Store as negative to mark user-set (system will not auto-recalculate)
                    series.ReleaseCadenceDays = -Math.Abs(request.CadenceDays.Value);
                }
                else
                {
                    // Clear user override — allow system to recalculate
                    series.ReleaseCadenceDays = null;
                }

                await _db.SaveChangesAsync(token).ConfigureAwait(false);

                // Re-evaluate health alerts for this series with the new cadence
                var settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
                // We need to load the series with its Sources for evaluation
                var seriesWithSources = await _db.Series
                    .Include(s => s.Sources)
                    .FirstOrDefaultAsync(s => s.Id == id, token)
                    .ConfigureAwait(false);

                if (seriesWithSources != null)
                {
                    await _statusEvaluation.EvaluateSingleSeriesAsync(seriesWithSources, settings, token).ConfigureAwait(false);
                }

                return Ok(new
                {
                    releaseCadenceDays = series.ReleaseCadenceDays.HasValue
                        ? (int?)Math.Abs(series.ReleaseCadenceDays.Value)
                        : null,
                    isUserSet = series.ReleaseCadenceDays.HasValue && series.ReleaseCadenceDays.Value < 0,
                    message = "Cadence updated successfully"
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error setting cadence for series {SeriesId}: {Message}", id, ex.Message);
                return StatusCode(500, new { error = ex.Message });
            }
        }
    }
}
