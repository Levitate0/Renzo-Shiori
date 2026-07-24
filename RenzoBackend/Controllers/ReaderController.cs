using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.ReadState;
using RenzoBackend.Services.Reader;
using RenzoBackend.Services.ReadState;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Controllers;

/// <summary>
/// Built-in reader API: chapter lists with read state, page metadata (smart
/// mode detection), page streaming from library archives, live preview
/// reading for Browse items, progress/bookmarks, and Suwayomi backup import.
/// Page image endpoints are loaded via &lt;img&gt; tags, so they authenticate
/// with the short-lived image-scoped ?token= (handled by AuthMiddleware).
/// </summary>
[ApiController]
[Route("api/reader")]
public class ReaderController : ControllerBase
{
    private readonly ReaderService _reader;
    private readonly ReaderPreviewService _preview;
    private readonly ReadStateService _readState;
    private readonly AppDbContext _db;
    private readonly ILogger _logger;

    public ReaderController(ReaderService reader, ReaderPreviewService preview, ReadStateService readState,
        AppDbContext db, ILogger<ReaderController> logger)
    {
        _reader = reader;
        _preview = preview;
        _readState = readState;
        _db = db;
        _logger = logger;
    }

    private UserEntity? CurrentUser => HttpContext.Items["User"] as UserEntity;
    private string CurrentUsername => CurrentUser?.Username ?? "";
    private Guid CurrentUserId => CurrentUser?.Id ?? Guid.Empty;
    private bool IsOwnerLevel => CurrentUser?.Level == Models.Enums.UserLevel.Owner;

    /// <summary>
    /// Per-user library isolation: denies reading a series that belongs to
    /// another user's library (image endpoints authenticate via the short-lived
    /// image-scoped ?token=, which still resolves a real user via AuthMiddleware).
    /// </summary>
    private async Task<ActionResult?> DenyAccessAsync(Guid seriesId, CancellationToken token)
    {
        Guid? ownerId = await _db.Series.Where(s => s.Id == seriesId).Select(s => (Guid?)s.OwnerId)
            .FirstOrDefaultAsync(token).ConfigureAwait(false);
        if (ownerId == null)
            return NotFound();
        if (!Services.Series.SeriesQueryService.CanAccessSeries(ownerId.Value, CurrentUserId, IsOwnerLevel))
            return StatusCode(403, new { error = "This series belongs to another user's library." });
        return null;
    }

    // ── Library reading ────────────────────────────────────────────────

    [HttpGet("chapters")]
    [ProducesResponseType(typeof(ReaderChaptersDto), 200)]
    public async Task<ActionResult<ReaderChaptersDto>> GetChaptersAsync([FromQuery] Guid seriesId, CancellationToken token = default)
    {
        if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
        var result = await _reader.GetChaptersAsync(seriesId, CurrentUsername, token).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("chapter-info")]
    [ProducesResponseType(typeof(ReaderChapterInfoDto), 200)]
    public async Task<ActionResult<ReaderChapterInfoDto>> GetChapterInfoAsync([FromQuery] Guid seriesId, [FromQuery] string filename, CancellationToken token = default)
    {
        try
        {
            if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
            var result = await _reader.GetChapterInfoAsync(seriesId, DecodeFilename(filename), token).ConfigureAwait(false);
            return result == null ? NotFound() : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reading chapter info for {SeriesId}/{Filename}", seriesId, filename);
            return StatusCode(500, new { error = "Could not open the chapter archive." });
        }
    }

    [HttpGet("page")]
    public async Task<ActionResult> GetPageAsync([FromQuery] Guid seriesId, [FromQuery] string filename, [FromQuery] int page, CancellationToken token = default)
    {
        if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
        (Stream? stream, string contentType) = await _reader.GetPageAsync(seriesId, DecodeFilename(filename), page, token).ConfigureAwait(false);
        if (stream == null)
            return NotFound();
        Response.Headers.CacheControl = "private, max-age=3600";
        return File(stream, contentType);
    }

    // ── Progress / bookmarks ───────────────────────────────────────────

    [HttpPost("progress")]
    public async Task<ActionResult> SetProgressAsync([FromBody] ReaderProgressRequestDto req, CancellationToken token = default)
    {
        UserEntity? user = CurrentUser;
        SeriesEntity? series = await _db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.SeriesId, token).ConfigureAwait(false);
        if (series == null)
            return NotFound();
        if (!Services.Series.SeriesQueryService.CanAccessSeries(series.OwnerId, CurrentUserId, IsOwnerLevel))
            return StatusCode(403, new { error = "This series belongs to another user's library." });
        // Progress tracks the live position both ways — reading forward raises it,
        // scrolling back lowers it. Completion stays sticky (see SetReadState);
        // explicit un-read goes through /mark.
        _readState.SetReadState(CurrentUsername, user?.Id ?? Guid.Empty, series.Id, req.Filename ?? "",
            null, "Renzō Reader", series.StoragePath, req.ChapterNumber, req.LastReadPage, req.TotalPages, updateLower: true);
        return Ok(new { success = true });
    }

    [HttpPost("mark")]
    public async Task<ActionResult> MarkAsync([FromBody] ReaderMarkRequestDto req, CancellationToken token = default)
    {
        UserEntity? user = CurrentUser;
        SeriesEntity? series = await _db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.SeriesId, token).ConfigureAwait(false);
        if (series == null)
            return NotFound();
        if (!Services.Series.SeriesQueryService.CanAccessSeries(series.OwnerId, CurrentUserId, IsOwnerLevel))
            return StatusCode(403, new { error = "This series belongs to another user's library." });
        foreach (decimal number in req.ChapterNumbers)
            _readState.SetCompleted(CurrentUsername, user?.Id ?? Guid.Empty, series.Id, series.StoragePath, number, req.Read);
        return Ok(new { success = true });
    }

    [HttpPost("bookmark")]
    public async Task<ActionResult> BookmarkAsync([FromBody] ReaderBookmarkRequestDto req, CancellationToken token = default)
    {
        SeriesEntity? series = await _db.Series.AsNoTracking().FirstOrDefaultAsync(s => s.Id == req.SeriesId, token).ConfigureAwait(false);
        if (series == null)
            return NotFound();
        if (!Services.Series.SeriesQueryService.CanAccessSeries(series.OwnerId, CurrentUserId, IsOwnerLevel))
            return StatusCode(403, new { error = "This series belongs to another user's library." });
        _readState.SetBookmark(CurrentUsername, series.StoragePath, req.ChapterNumber, req.Bookmarked);
        return Ok(new { success = true });
    }

    // ── Preview reading (Browse items, nothing stored) ─────────────────

    [HttpGet("preview/chapters")]
    [ProducesResponseType(typeof(PreviewChaptersDto), 200)]
    public async Task<ActionResult<PreviewChaptersDto>> GetPreviewChaptersAsync([FromQuery] string mihonId, CancellationToken token = default)
    {
        try
        {
            var result = await _preview.GetChaptersAsync(mihonId, token).ConfigureAwait(false);
            return result == null ? NotFound() : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview chapters failed for {MihonId}", mihonId);
            return StatusCode(502, new { error = "The source did not return a chapter list." });
        }
    }

    [HttpGet("preview/pages")]
    [ProducesResponseType(typeof(PreviewPagesDto), 200)]
    public async Task<ActionResult<PreviewPagesDto>> GetPreviewPagesAsync([FromQuery] string mihonId, [FromQuery] int chapter, CancellationToken token = default)
    {
        try
        {
            var result = await _preview.GetPagesAsync(mihonId, chapter, CurrentUser?.Id, token).ConfigureAwait(false);
            return result == null ? NotFound() : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview pages failed for {MihonId} ch {Chapter}", mihonId, chapter);
            return StatusCode(502, new { error = SourceFailureMessage(mihonId) });
        }
    }

    /// <summary>
    /// Some extensions (e.g. Lunar Anime) serve encrypted/obfuscated page lists
    /// that their Mihon code can't decode in this runtime — the failure comes
    /// from inside the extension, not from Renzo. Tell the user that plainly
    /// instead of surfacing a raw Java exception.
    /// </summary>
    private string SourceFailureMessage(string mihonId)
    {
        string source = _db.LatestSeries.AsNoTracking()
            .Where(a => a.MihonId == mihonId)
            .Select(a => a.Provider)
            .FirstOrDefault() ?? "This source";
        return $"{source} wouldn't hand over the pages — that source protects them in a way the reader can't decode. " +
               "Preview reading works on most other sources; add the series to your library to download it instead.";
    }

    [HttpGet("preview/page")]
    public async Task<ActionResult> GetPreviewPageAsync([FromQuery] string mihonId, [FromQuery] int chapter, [FromQuery] int page, CancellationToken token = default)
    {
        try
        {
            (Stream? stream, string contentType) = await _preview.GetPageImageAsync(mihonId, chapter, page, CurrentUser?.Id, token).ConfigureAwait(false);
            if (stream == null)
                return NotFound();
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(stream, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Preview page failed for {MihonId} ch {Chapter} p {Page}", mihonId, chapter, page);
            return StatusCode(502, new { error = SourceFailureMessage(mihonId) });
        }
    }

    // ── Library streaming (read a not-yet-downloaded chapter live) ─────

    [HttpGet("stream/pages")]
    [ProducesResponseType(typeof(PreviewPagesDto), 200)]
    public async Task<ActionResult<PreviewPagesDto>> GetStreamPagesAsync([FromQuery] Guid seriesId, [FromQuery] decimal chapter, [FromQuery] bool refresh = false, CancellationToken token = default)
    {
        try
        {
            if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
            var result = await _preview.GetLibraryStreamPagesAsync(seriesId, chapter, CurrentUser?.Id, refresh, token).ConfigureAwait(false);
            return result == null ? NotFound() : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream pages failed for {SeriesId} ch {Chapter}", seriesId, chapter);
            return StatusCode(502, new { error = "The source wouldn't hand over this chapter's pages." });
        }
    }

    [HttpGet("stream/page")]
    public async Task<ActionResult> GetStreamPageAsync([FromQuery] Guid seriesId, [FromQuery] decimal chapter, [FromQuery] int page, CancellationToken token = default)
    {
        try
        {
            if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
            (Stream? stream, string contentType) = await _preview.GetLibraryStreamPageImageAsync(seriesId, chapter, page, CurrentUser?.Id, token).ConfigureAwait(false);
            if (stream == null)
                return NotFound();
            Response.Headers.CacheControl = "private, max-age=86400";
            return File(stream, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream page failed for {SeriesId} ch {Chapter} p {Page}", seriesId, chapter, page);
            return StatusCode(502, new { error = "The source wouldn't hand over this page." });
        }
    }

    /// <summary>
    /// Clears the in-memory cache of streamed (web-pulled) page images. Downloaded
    /// chapters are unaffected. Useful when a source served stale/broken pages.
    /// </summary>
    [HttpPost("clear-stream-cache")]
    public ActionResult ClearStreamCache()
    {
        long freed = _preview.ClearStreamCache();
        _logger.LogInformation("Cleared reader stream image cache ({Count} images).", freed);
        return Ok(new { success = true, cleared = freed });
    }

    // ── Suwayomi backup import (read-state sync) ───────────────────────

    /// <summary>
    /// Imports read progress, completed chapters, and bookmarks from a
    /// Tachiyomi/Suwayomi .tachibk backup. Series are matched by title
    /// against the library (including every source's title); chapters by
    /// number. Existing local progress is never downgraded.
    /// </summary>
    [HttpPost("import-backup")]
    [RequestSizeLimit(200 * 1024 * 1024)]
    [ProducesResponseType(typeof(BackupImportResultDto), 200)]
    public async Task<ActionResult<BackupImportResultDto>> ImportBackupAsync(IFormFile file, CancellationToken token = default)
    {
        if (file == null || file.Length == 0)
            return BadRequest(new { error = "No backup file provided" });

        List<TachibkManga> backup;
        try
        {
            await using Stream s = file.OpenReadStream();
            backup = TachibkParser.Parse(s);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse backup file {Name}", file.FileName);
            return BadRequest(new { error = "Could not parse the backup — is it a .tachibk/.proto.gz file?" });
        }

        List<SeriesEntity> library = await _db.Series.Include(s => s.Sources)
            .AsNoTracking().ToListAsync(token).ConfigureAwait(false);

        string username = CurrentUsername;
        var result = new BackupImportResultDto { BackupSeries = backup.Count };

        foreach (TachibkManga manga in backup)
        {
            if (string.IsNullOrWhiteSpace(manga.Title))
                continue;
            bool hasState = manga.Chapters.Any(c => c.Read || c.Bookmark || c.LastPageRead > 0);
            if (!hasState)
                continue;

            SeriesEntity? match = library.FirstOrDefault(s =>
                s.Title.AreStringSimilar(manga.Title) ||
                s.Sources.Any(p => !string.IsNullOrEmpty(p.Title) && p.Title.AreStringSimilar(manga.Title)));
            if (match == null)
            {
                result.Unmatched.Add(manga.Title);
                continue;
            }
            result.MatchedSeries++;

            // Never downgrade what's already read locally.
            List<ChapterReadState> existing = _readState.GetSeriesReadStates(username, match.StoragePath);
            Dictionary<decimal, int?> pageCounts = match.Sources.SelectMany(p => p.Chapters)
                .Where(c => c.Number != null && c.PageCount > 0)
                .GroupBy(c => c.Number!.Value)
                .ToDictionary(g => g.Key, g => g.Max(c => c.PageCount));

            var incoming = new List<ChapterReadState>();
            foreach (TachibkChapter ch in manga.Chapters)
            {
                if (ch.Number < 0 || (!ch.Read && !ch.Bookmark && ch.LastPageRead <= 0))
                    continue;
                ChapterReadState? local = existing.FirstOrDefault(e => e.ChapterNumber == ch.Number);

                float progress = 0f;
                if (ch.Read)
                    progress = 1f;
                else if (ch.LastPageRead > 0 && pageCounts.TryGetValue(ch.Number, out int? pc) && pc > 0)
                    progress = Math.Min(1f, (float)ch.LastPageRead / pc.Value);

                bool completed = ch.Read || (local?.IsCompleted ?? false);
                if (local != null)
                    progress = Math.Max(progress, local.Progress);

                incoming.Add(new ChapterReadState
                {
                    ChapterNumber = ch.Number,
                    IsCompleted = completed,
                    Progress = completed ? 1f : progress,
                    Bookmarked = ch.Bookmark || (local?.Bookmarked ?? false),
                    LastReadDeviceName = "Suwayomi import",
                    LastReadAt = local?.LastReadAt ?? DateTime.UtcNow
                });
                result.UpdatedChapters++;
                if (ch.Bookmark)
                    result.Bookmarks++;
            }

            if (incoming.Count > 0)
            {
                _readState.ImportUserReadStates(match.StoragePath,
                    [new UserReadStateSnapshot { Username = username, Chapters = incoming }]);
            }
        }

        _logger.LogInformation("Suwayomi backup import: {Matched}/{Total} series matched, {Chapters} chapter states, {Bookmarks} bookmarks",
            result.MatchedSeries, result.BackupSeries, result.UpdatedChapters, result.Bookmarks);
        return Ok(result);
    }

    /// <summary>
    /// Chapter archive filenames arrive base64url-encoded (they can contain
    /// characters that break query strings).
    /// </summary>
    private static string DecodeFilename(string b64) => OpdsController.DecodeBase64Url(b64);
}
