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
            null, "Renzo Shiori Reader", series.StoragePath, req.ChapterNumber, req.LastReadPage, req.TotalPages, updateLower: true);
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
            // Source bytes on our origin: never as a document (see SafePageContentType).
            contentType = HttpExtensions.SafePageContentType(stream, contentType);
            Response.AddImageSafetyHeaders($"page{page}", contentType);
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
    /// <param name="refresh">Re-fetch this chapter's PAGES from the source, bypassing the
    /// cached page list and dropping the chapter's cached page images.</param>
    /// <param name="refreshChapters">Additionally re-fetch the source's whole chapter LIST.
    /// Much more expensive — one 20s-bounded source call per candidate source — and only
    /// meaningful when the question is whether a chapter has appeared in the listing at all
    /// (a coin-gated chapter after purchase). A plain reopen must not set this.</param>
    /// <param name="nextSource">"Switch source": reload from the next source instead of the one that served it.</param>
    public async Task<ActionResult<PreviewPagesDto>> GetStreamPagesAsync([FromQuery] Guid seriesId, [FromQuery] decimal chapter, [FromQuery] bool refresh = false, [FromQuery] bool refreshChapters = false, [FromQuery] bool nextSource = false, CancellationToken token = default)
    {
        try
        {
            if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
            var result = await _preview.GetLibraryStreamPagesAsync(seriesId, chapter, CurrentUser?.Id, refresh, refreshChapters, token, nextSource).ConfigureAwait(false);
            return result == null ? NotFound() : Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stream pages failed for {SeriesId} ch {Chapter}", seriesId, chapter);
            return StatusCode(502, new { error = "The source wouldn't hand over this chapter's pages." });
        }
    }

    /// <summary>
    /// The reader reached a paid chapter: buy it on the source when auto-buy is on
    /// for this series or account. Runs in the background ("running"); poll the
    /// GET for the answer, which says why not when it couldn't, for the manual
    /// purchase screen. Clients call it only for the chapter actually on screen,
    /// never a prefetch.
    /// </summary>
    [HttpPost("auto-buy")]
    [ProducesResponseType(typeof(Services.Reader.AutoBuy.AutoBuyResultDto), 200)]
    public async Task<ActionResult<Services.Reader.AutoBuy.AutoBuyResultDto>> AutoBuyAsync([FromQuery] Guid seriesId, [FromQuery] decimal chapter, [FromQuery] bool dryRun = false, CancellationToken token = default)
    {
        if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
        var runner = HttpContext.RequestServices.GetRequiredService<Services.Reader.AutoBuy.AutoBuyRunner>();
        return Ok(await runner.StartAsync(seriesId, chapter, CurrentUser, dryRun && IsOwnerLevel, token).ConfigureAwait(false));
    }

    [HttpGet("auto-buy")]
    [ProducesResponseType(typeof(Services.Reader.AutoBuy.AutoBuyResultDto), 200)]
    public async Task<ActionResult<Services.Reader.AutoBuy.AutoBuyResultDto>> AutoBuyStatusAsync([FromQuery] Guid seriesId, [FromQuery] decimal chapter, CancellationToken token = default)
    {
        if (await DenyAccessAsync(seriesId, token).ConfigureAwait(false) is { } deny) return deny;
        var r = HttpContext.RequestServices.GetRequiredService<Services.Reader.AutoBuy.AutoBuyRunner>().Status(seriesId, chapter);
        return r == null ? NotFound() : Ok(r);
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
            contentType = HttpExtensions.SafePageContentType(stream, contentType);
            Response.AddImageSafetyHeaders($"page{page}", contentType);
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
    /// The cache is shared, so only the Owner (or guest mode) wipes all of it;
    /// anyone else drops just the images their own reads pulled — both clients
    /// call this on every reader exit, so it must stay open to every level.
    /// </summary>
    [HttpPost("clear-stream-cache")]
    public ActionResult ClearStreamCache()
    {
        UserEntity? user = CurrentUser;
        if (user == null && HttpContext.Items["AuthEnabled"] is true)
            return Unauthorized();
        bool everything = user == null || user.Level >= Models.Enums.UserLevel.Owner;
        long freed = _preview.ClearStreamCache(everything ? null : user!.Id);
        _logger.LogInformation("Cleared reader stream image cache ({Count} images, {Scope}).", freed,
            everything ? "all users" : "own reads");
        return Ok(new { success = true, cleared = freed });
    }

    /// <summary>
    /// Drops a series' streamed page images for every chapter NOT listed in
    /// <c>keep</c>. The paged reader sends the chapter on screen and its two
    /// neighbours each time it moves, so its cache follows the reader instead of
    /// accumulating the whole session. Downloaded chapters are unaffected.
    /// Scoped like clear-stream-cache: below Owner only the caller's own pulls are
    /// dropped, so nobody can evict another reader's pages (and force every
    /// reader of that series to re-fetch from the source) by naming its id.
    /// </summary>
    [HttpPost("trim-stream-cache")]
    public ActionResult TrimStreamCache([FromBody] TrimStreamCacheRequest request)
    {
        if (request == null || request.SeriesId == Guid.Empty || request.Keep == null || request.Keep.Count == 0)
            return BadRequest(new { error = "seriesId and at least one chapter to keep are required." });
        UserEntity? user = CurrentUser;
        if (user == null && HttpContext.Items["AuthEnabled"] is true)
            return Unauthorized();
        bool everyone = user == null || user.Level >= Models.Enums.UserLevel.Owner;
        int freed = _preview.TrimStreamCache(request.SeriesId, request.Keep, everyone ? null : user!.Id);
        if (freed > 0)
            _logger.LogInformation("Trimmed {Count} streamed page image(s) for series {SeriesId}, keeping chapters {Keep}.", freed, request.SeriesId, string.Join(", ", request.Keep));
        return Ok(new { success = true, cleared = freed });
    }

    public sealed class TrimStreamCacheRequest
    {
        public Guid SeriesId { get; set; }
        public List<decimal> Keep { get; set; } = new();
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

        // One import per account, and a few at a time overall: each one can hold
        // up to 64 MB inflated plus its parse, so many parallel uploads were a way
        // to push the container out of memory.
        Guid importer = CurrentUserId;
        if (!RunningImports.TryAdd(importer, 0))
            return StatusCode(429, new { error = "A backup import is already running for your account." });
        try
        {
            if (!await ImportSlots.WaitAsync(TimeSpan.FromSeconds(30), token).ConfigureAwait(false))
                return StatusCode(429, new { error = "Other backup imports are running. Try again in a moment." });
            try
            {
                return await ImportBackupCoreAsync(file, token).ConfigureAwait(false);
            }
            finally
            {
                ImportSlots.Release();
            }
        }
        finally
        {
            RunningImports.TryRemove(importer, out _);
        }
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> RunningImports = new();
    private static readonly SemaphoreSlim ImportSlots = new(2, 2);

    private async Task<ActionResult<BackupImportResultDto>> ImportBackupCoreAsync(IFormFile file, CancellationToken token)
    {
        List<TachibkManga> backup;
        try
        {
            await using Stream s = file.OpenReadStream();
            backup = TachibkParser.Parse(s);
        }
        catch (BackupTooLargeException ex)
        {
            return BadRequest(new { error = $"That backup is too large to import ({ex.Message})." });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse backup file {Name}", file.FileName);
            return BadRequest(new { error = "Could not parse the backup — is it a .tachibk/.proto.gz file?" });
        }

        List<SeriesEntity> library = await ImportableSeries(_db.Series.Include(s => s.Sources), CurrentUserId)
            .AsNoTracking().ToListAsync(token).ConfigureAwait(false);

        string username = CurrentUsername;
        var result = new BackupImportResultDto { BackupSeries = backup.Count };

        // A backup can list the same title (or titles matching one series) many
        // times. Match each distinct title once, merge every entry's chapters per
        // matched series, and write each series' renzo.json once — 20k duplicate
        // entries used to mean 20k fuzzy scans and 20k whole-file rewrites.
        var matchByTitle = new Dictionary<string, SeriesEntity?>(StringComparer.Ordinal);
        var perSeries = new Dictionary<Guid, (SeriesEntity series, Dictionary<decimal, TachibkChapter> chapters)>();
        foreach (TachibkManga manga in backup)
        {
            if (string.IsNullOrWhiteSpace(manga.Title))
                continue;
            bool hasState = manga.Chapters.Any(c => c.Read || c.Bookmark || c.LastPageRead > 0);
            if (!hasState)
                continue;

            if (!matchByTitle.TryGetValue(manga.Title, out SeriesEntity? match))
            {
                match = library.FirstOrDefault(s =>
                    s.Title.AreStringSimilar(manga.Title) ||
                    s.Sources.Any(p => !string.IsNullOrEmpty(p.Title) && p.Title.AreStringSimilar(manga.Title)));
                matchByTitle[manga.Title] = match;
            }
            if (match == null)
            {
                result.Unmatched.Add(manga.Title);
                continue;
            }
            result.MatchedSeries++;

            if (!perSeries.TryGetValue(match.Id, out var acc))
                perSeries[match.Id] = acc = (match, new Dictionary<decimal, TachibkChapter>());
            foreach (TachibkChapter ch in manga.Chapters)
            {
                if (ch.Number < 0 || (!ch.Read && !ch.Bookmark && ch.LastPageRead <= 0))
                    continue;
                // Same merge as the parser's within one entry: read/bookmark OR-ed,
                // furthest page kept.
                if (acc.chapters.TryGetValue(ch.Number, out TachibkChapter? seen))
                {
                    seen.Read |= ch.Read;
                    seen.Bookmark |= ch.Bookmark;
                    seen.LastPageRead = Math.Max(seen.LastPageRead, ch.LastPageRead);
                }
                else
                {
                    acc.chapters[ch.Number] = new TachibkChapter
                    {
                        Number = ch.Number, Name = ch.Name, Read = ch.Read,
                        Bookmark = ch.Bookmark, LastPageRead = ch.LastPageRead,
                    };
                }
            }
        }

        foreach ((SeriesEntity match, Dictionary<decimal, TachibkChapter> chapters) in perSeries.Values)
        {
            // Never downgrade what's already read locally.
            // Indexed by number: a per-chapter linear search here was O(n²) on a
            // long series (see TachibkParser's caps for the other half).
            var existing = new Dictionary<decimal, ChapterReadState>();
            foreach (ChapterReadState e in _readState.GetSeriesReadStates(username, match.StoragePath))
                existing.TryAdd(e.ChapterNumber, e);
            Dictionary<decimal, int?> pageCounts = match.Sources.SelectMany(p => p.Chapters)
                .Where(c => c.Number != null && c.PageCount > 0)
                .GroupBy(c => c.Number!.Value)
                .ToDictionary(g => g.Key, g => g.Max(c => c.PageCount));

            var incoming = new List<ChapterReadState>();
            foreach (TachibkChapter ch in chapters.Values)
            {
                existing.TryGetValue(ch.Number, out ChapterReadState? local);

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
    /// The series a backup import may write read state into: the caller's own
    /// library, plus legacy ownerless rows (which CanAccessSeries also allows).
    /// It used to match across EVERY library, so one user's import wrote
    /// progress into series other users own.
    /// </summary>
    internal static IQueryable<SeriesEntity> ImportableSeries(IQueryable<SeriesEntity> series, Guid ownerId) =>
        series.Where(s => s.OwnerId == ownerId || s.OwnerId == Guid.Empty);

    /// <summary>
    /// Chapter archive filenames arrive base64url-encoded (they can contain
    /// characters that break query strings).
    /// </summary>
    private static string DecodeFilename(string b64) => OpdsController.DecodeBase64Url(b64);
}
