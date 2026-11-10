using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Images;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Controllers
{
    [ApiController]
    [Route("api/downloads")]
    [Produces("application/json")]
    public class DownloadsController : ControllerBase
    {
        private readonly DownloadQueryService _downloadQuery;
        private readonly DownloadCommandService _downloadCommand;
        private readonly ThumbCacheService _thumbs;
        private readonly AppDbContext _db;
        private readonly ILogger _logger;

        public DownloadsController(ILogger<DownloadsController> logger,
            ThumbCacheService thumbs,
            DownloadQueryService downloadQuery,
            DownloadCommandService downloadCommand,
            AppDbContext db)
        {
            _downloadQuery = downloadQuery;
            _downloadCommand = downloadCommand;
            _thumbs = thumbs;
            _db = db;
            _logger = logger;
        }

        private UserEntity? CurrentUser => HttpContext.Items["User"] as UserEntity;
        private Guid CurrentUserId => CurrentUser?.Id ?? Guid.Empty;
        private bool IsOwnerLevel => CurrentUser?.Level == UserLevel.Owner;

        [HttpGet("series")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<List<DownloadInfoDto>>> GetDownloadsForSeriesAsync([FromQuery] Guid seriesId, CancellationToken token = default)
        {
            try
            {
                Guid? ownerId = await _db.Series.Where(s => s.Id == seriesId).Select(s => (Guid?)s.OwnerId).FirstOrDefaultAsync(token).ConfigureAwait(false);
                if (ownerId == null)
                    return NotFound(new { success = false, error = "Series not found" });
                if (!Services.Series.SeriesQueryService.CanAccessSeries(ownerId.Value, CurrentUserId, IsOwnerLevel))
                    return StatusCode(403, new { success = false, error = "This series belongs to another user's library." });

                var sources = await _downloadQuery.GetDownloadsForSeriesAsync(seriesId, token).ConfigureAwait(false);
                await _thumbs.PopulateThumbsAsync(sources, "/api/image/", token).ConfigureAwait(false);
                return Ok(sources);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving downloads for Series: {Message}", ex.Message);
                return StatusCode(500, new { error = "An error occurred while retrieving downloads for Series" });
            }
        }

        /// <summary>
        /// Each user's download queue is isolated to their own library. Owner-level
        /// accounts may pass ?viewAll=true to see every user's queue.
        /// </summary>
        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<DownloadInfoListDto>> GetDownloadsAsync([FromQuery] QueueStatus status, int limit = 100, string? keyword = null, [FromQuery] bool viewAll = false, CancellationToken token = default)
        {
            try
            {
                var result = await _downloadQuery.GetDownloadsAsync(status, limit, keyword, CurrentUserId, viewAll && IsOwnerLevel, token).ConfigureAwait(false);
                await _thumbs.PopulateThumbsAsync(result.Downloads, "/api/image/", token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving downloads for Series: {Message}", ex.Message);
                return StatusCode(500, new { error = "An error occurred while retrieving downloads for Series" });
            }
        }

        [HttpGet("metrics")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<DownloadsMetricsDto>> GetDownloadsMetricsAsync([FromQuery] bool viewAll = false, CancellationToken token = default)
        {
            try
            {
                var result = await _downloadQuery.GetDownloadsMetricsAsync(CurrentUserId, viewAll && IsOwnerLevel, token).ConfigureAwait(false);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving downloads metrics: {Message}", ex.Message);
                return StatusCode(500, new { error = "An error occurred while retrieving downloads metrics" });
            }
        }

        /// <summary>
        /// Queue page → "Clear completed": drops every finished download row in the
        /// requester's library (or every library for an Owner with ?viewAll=true).
        /// Only the history goes; the chapter files stay, and release cadence falls
        /// back to the providers' chapter dates, as it does after the daily cleanup.
        /// </summary>
        [HttpDelete("completed")]
        public async Task<ActionResult> ClearCompletedAsync([FromQuery] bool viewAll = false, CancellationToken token = default)
        {
            try
            {
                HashSet<string>? owned = await _downloadQuery.GetOwnedSeriesKeysAsync(CurrentUserId, viewAll && IsOwnerLevel, token).ConfigureAwait(false);
                IQueryable<EnqueueEntity> rows = _db.Queues.Where(q => q.JobType == JobType.Download && q.Status == QueueStatus.Completed);
                if (owned != null)
                    rows = rows.Where(q => q.ExtraKey != null && owned.Contains(q.ExtraKey));
                int removed = await rows.ExecuteDeleteAsync(token).ConfigureAwait(false);
                return Ok(new { removed });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing completed downloads: {Message}", ex.Message);
                return StatusCode(500, new { error = "An error occurred while clearing completed downloads." });
            }
        }

        [HttpPatch]
        public async Task<ActionResult> ManageErrorDownloadAsync([FromQuery]Guid id, [FromQuery]ErrorDownloadAction action, CancellationToken token = default)
        {
            try
            {
                EnqueueEntity? job = await _db.Queues.FirstOrDefaultAsync(q => q.Id == id, token).ConfigureAwait(false);
                if (job == null)
                    return NotFound(new { error = "Download not found" });
                if (!IsOwnerLevel && !string.IsNullOrEmpty(job.ExtraKey) && Guid.TryParse(job.ExtraKey, out Guid seriesId))
                {
                    Guid? ownerId = await _db.Series.Where(s => s.Id == seriesId).Select(s => (Guid?)s.OwnerId).FirstOrDefaultAsync(token).ConfigureAwait(false);
                    if (ownerId != null && !Services.Series.SeriesQueryService.CanAccessSeries(ownerId.Value, CurrentUserId, false))
                        return StatusCode(403, new { error = "This download belongs to another user's library." });
                }

                await _downloadCommand.ManageErrorDownloadAsync(id, action, token).ConfigureAwait(false);
                return Ok();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error managing download: {Message}", ex.Message);
                return StatusCode(500, new { error = "An error occurred while managing the download." });
            }
        }
    }
}
