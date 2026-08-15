using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Bridge;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;
using RenzoBackend.Services.Search;
using System.Text.RegularExpressions;

namespace RenzoBackend.Services.Reader;

/// <summary>
/// Preview reading for Browse items that are NOT in the library: fetches
/// chapter lists and page images live from the source through the Mihon
/// bridge, holding nothing on disk. Chapter/page lists are memory-cached for
/// 30 minutes so paging through a chapter doesn't re-hit the source's
/// list endpoints for every image.
/// </summary>
public class ReaderPreviewService
{
    private readonly AppDbContext _db;
    private readonly MihonBridgeService _mihon;
    private readonly IMemoryCache _cache;
    private readonly SiteAuth.SiteAuthService _siteAuth;
    private readonly Series.VComicsContentService _vcomics;
    private readonly ILogger _logger;

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    // A single page image must not be able to hang forever: an un-timed source
    // fetch holds the browser's connection open, and once a few stall the reader
    // stops loading images entirely until it's re-opened. Bounding each fetch lets
    // a slow page fail fast (the client just retries) instead of freezing.
    private static readonly TimeSpan StreamImageTimeout = TimeSpan.FromSeconds(30);

    // Each source's chapter-list / page-list fetch is bounded so a slow or dead
    // source is skipped quickly and the next permanent source is tried.
    private static readonly TimeSpan StreamSourceTimeout = TimeSpan.FromSeconds(20);

    // Validation probe (page 0 image) when picking the source to stream from: a
    // source can return a page LIST but have a dead/timing-out image host, so we
    // fetch page 0 to confirm images actually load before committing to it. Kept
    // shorter than StreamImageTimeout so a bad source falls through to the next
    // one quickly instead of stalling the chapter open.
    private static readonly TimeSpan StreamProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// EnsureLoggedInAsync performs a REAL login POST every call. The locked-chapter
    /// poll retries page fetches repeatedly, and a chapter that stays locked would
    /// re-login on every attempt — spamming the coin site's login endpoint (ban
    /// risk). Gate attempts to once per provider per minute; within the window the
    /// existing session is reused without a fresh POST.
    /// </summary>
    private async Task<bool> RateLimitedReloginAsync(Guid userId, string provider, CancellationToken token)
    {
        string gate = $"siteauth:relogin:{userId}:{provider}";
        if (_cache.TryGetValue(gate, out bool lastResult))
            return lastResult;
        bool ok = await _siteAuth.EnsureLoggedInAsync(userId, provider, token).ConfigureAwait(false);
        _cache.Set(gate, ok, TimeSpan.FromSeconds(60));
        return ok;
    }

    private readonly StreamImageCache _imageCache;

    /// <summary>
    /// Clears the in-memory cache of streamed (web-pulled) page images. Downloaded
    /// chapters are unaffected — they read from their CBZ on disk, not this cache.
    /// Returns the number of cached images dropped.
    /// </summary>
    public long ClearStreamCache() => _imageCache.Clear();

    public ReaderPreviewService(AppDbContext db, MihonBridgeService mihon, IMemoryCache cache,
        SiteAuth.SiteAuthService siteAuth, StreamImageCache imageCache,
        Series.VComicsContentService vcomics, ILogger<ReaderPreviewService> logger)
    {
        _db = db;
        _mihon = mihon;
        _cache = cache;
        _siteAuth = siteAuth;
        _imageCache = imageCache;
        _vcomics = vcomics;
        _logger = logger;
    }

    private async Task<(LatestSerieEntity entity, ISourceInterop source)?> ResolveAsync(string mihonId, CancellationToken token)
    {
        LatestSerieEntity? entity = await _db.LatestSeries.AsNoTracking()
            .FirstOrDefaultAsync(a => a.MihonId == mihonId, token).ConfigureAwait(false);
        if (entity == null || string.IsNullOrEmpty(entity.MihonProviderId))
            return null;
        ISourceInterop src = await _mihon.SourceFromProviderIdAsync(entity.MihonProviderId, token).ConfigureAwait(false);
        if (src == null)
            return null;
        return (entity, src);
    }

    /// <summary>
    /// The source manga for a browse row. Prefers the stored bridge payload, which
    /// carries everything the extension parsed; falls back to rebuilding one from
    /// the row's own columns.
    ///
    /// The fallback matters because rows persisted by the live catalog/search paths
    /// were stored WITHOUT that payload, and <c>ToManga()</c> reads nothing else —
    /// so those rows were browsable but returned a bare 404 the moment they were
    /// opened. An extension only needs the URL to fetch a chapter list, so a row
    /// with a URL is readable whether or not the richer payload survived.
    /// </summary>
    private static Manga? ToSourceManga(LatestSerieEntity entity)
    {
        Manga? manga = entity.ToManga();
        if (manga != null)
            return manga;
        if (string.IsNullOrEmpty(entity.Url))
            return null;
        return new Manga
        {
            Url = entity.Url,
            Title = entity.Title,
            Artist = entity.Artist,
            Author = entity.Author,
            Description = entity.Description,
            Genre = entity.Genre.Count > 0 ? string.Join(", ", entity.Genre) : null,
            ThumbnailUrl = entity.ThumbnailUrl,
        };
    }

    public async Task<PreviewChaptersDto?> GetChaptersAsync(string mihonId, CancellationToken token = default)
    {
        var resolved = await ResolveAsync(mihonId, token).ConfigureAwait(false);
        if (resolved == null)
        {
            _logger.LogWarning(
                "Preview chapters: no browse row stored for {MihonId} — it can't be opened. "
                + "Rows only reachable through a live search were never persisted.", mihonId);
            return null;
        }
        (LatestSerieEntity entity, ISourceInterop src) = resolved.Value;

        Manga? manga = ToSourceManga(entity);
        if (manga == null)
        {
            _logger.LogWarning("Preview chapters: browse row {MihonId} ({Provider}) has no URL to fetch from.",
                mihonId, entity.Provider);
            return null;
        }

        List<ParsedChapter>? chapters = _cache.Get<List<ParsedChapter>>($"pv:ch:{mihonId}");
        if (chapters == null)
        {
            chapters = await src.GetChaptersAsync(manga, token).ConfigureAwait(false);
            if (chapters == null)
                return null;
            _cache.Set($"pv:ch:{mihonId}", chapters, CacheTtl);
        }

        return new PreviewChaptersDto
        {
            MihonId = mihonId,
            Title = entity.Title,
            Chapters = chapters.Select((c, i) => new PreviewChapterDto
            {
                Index = i,
                Name = string.IsNullOrEmpty(c.ParsedName) ? c.Name : c.ParsedName,
                Number = c.ParsedNumber != 0 ? c.ParsedNumber : (decimal?)null,
                DateUpload = c.DateUpload == default ? null : c.DateUpload.UtcDateTime
            }).ToList()
        };
    }

    public async Task<PreviewPagesDto?> GetPagesAsync(string mihonId, int chapterIndex, Guid? userId = null, CancellationToken token = default)
    {
        List<Page>? pages = await GetPageListAsync(mihonId, chapterIndex, userId, token).ConfigureAwait(false);
        return pages == null ? null : new PreviewPagesDto { PageCount = pages.Count };
    }

    public async Task<(Stream? stream, string contentType)> GetPageImageAsync(string mihonId, int chapterIndex, int pageIndex, Guid? userId = null, CancellationToken token = default)
    {
        string imgKey = $"pv:img:{mihonId}:{chapterIndex}:{pageIndex}";
        if (_imageCache.TryGet(imgKey, out StreamImageCache.Entry cached))
            return (new MemoryStream(cached.Bytes, writable: false), cached.ContentType);

        List<Page>? pages = await GetPageListAsync(mihonId, chapterIndex, userId, token).ConfigureAwait(false);
        if (pages == null || pageIndex < 0 || pageIndex >= pages.Count)
            return (null, "");

        var resolved = await ResolveAsync(mihonId, token).ConfigureAwait(false);
        if (resolved == null)
            return (null, "");

        ContentTypeStream? img;
        try
        {
            img = await SourceTimeout
                .RunAsync(c => resolved.Value.source.GetPageImageAsync(pages[pageIndex], c), StreamImageTimeout, token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Preview page image timed out for {MihonId} ch {Chapter} page {Page}", mihonId, chapterIndex, pageIndex);
            return (null, "");
        }
        if (img == null)
            return (null, "");

        string ct = string.IsNullOrEmpty(img.ContentType) ? "image/jpeg" : img.ContentType;
        byte[] bytes = img.ToArray();
        _imageCache.Set(imgKey, bytes, ct);
        return (new MemoryStream(bytes, writable: false), ct);
    }

    private async Task<List<Page>?> GetPageListAsync(string mihonId, int chapterIndex, Guid? userId, CancellationToken token)
    {
        string key = $"pv:pg:{mihonId}:{chapterIndex}";
        List<Page>? pages = _cache.Get<List<Page>>(key);
        if (pages != null)
            return pages;

        // Every `return null` below surfaces to the caller as a bare 404 with no
        // body, which is indistinguishable from "that chapter doesn't exist" and
        // told us nothing when preview reading failed in the wild. Each one now
        // says which of them it was.
        var resolved = await ResolveAsync(mihonId, token).ConfigureAwait(false);
        if (resolved == null)
        {
            _logger.LogWarning("Preview: no usable source for {MihonId} — it may be uninstalled or disabled.", mihonId);
            return null;
        }
        (LatestSerieEntity entity, ISourceInterop src) = resolved.Value;

        string chapterKey = $"pv:ch:{mihonId}";
        List<ParsedChapter>? chapters = _cache.Get<List<ParsedChapter>>(chapterKey);
        bool fromCache = chapters != null;
        if (chapters == null)
        {
            Manga? manga = ToSourceManga(entity);
            if (manga == null)
            {
                _logger.LogWarning("Preview: browse row {MihonId} ({Provider}) has no URL to fetch from.", mihonId, entity.Provider);
                return null;
            }
            chapters = await src.GetChaptersAsync(manga, token).ConfigureAwait(false);
            if (chapters == null)
            {
                _logger.LogWarning("Preview: {Provider} returned no chapter list for {Title}.", entity.Provider, entity.Title);
                return null;
            }
            _cache.Set(chapterKey, chapters, CacheTtl);
        }

        // An index past the end of a CACHED list usually means the listing moved
        // under us — the source published or withdrew chapters after this list was
        // taken, and every index shifted. Re-ask once before giving up, rather
        // than 404ing until the 30-minute cache happens to expire.
        if (chapterIndex >= chapters.Count && fromCache)
        {
            Manga? manga = ToSourceManga(entity);
            List<ParsedChapter>? fresh = manga == null
                ? null
                : await src.GetChaptersAsync(manga, token).ConfigureAwait(false);
            if (fresh != null)
            {
                _logger.LogInformation(
                    "Preview: chapter index {Index} was past the cached list for {Provider} ({Old} chapters); re-fetched and got {New}.",
                    chapterIndex, entity.Provider, chapters.Count, fresh.Count);
                chapters = fresh;
                _cache.Set(chapterKey, chapters, CacheTtl);
            }
        }

        if (chapterIndex < 0 || chapterIndex >= chapters.Count)
        {
            _logger.LogWarning(
                "Preview: chapter index {Index} is out of range for {Provider} '{Title}', which lists {Count} chapter(s).",
                chapterIndex, entity.Provider, entity.Title, chapters.Count);
            return null;
        }

        pages = await src.GetPagesAsync(chapters[chapterIndex], token).ConfigureAwait(false);

        // A locked/paid chapter comes back empty when the site session has
        // lapsed. If the user has a login for this source, re-authenticate
        // (refreshing cookies in the shared jar) and try once more.
        if ((pages == null || pages.Count == 0) && userId != null)
        {
            bool relogged = await RateLimitedReloginAsync(userId.Value, entity.Provider, token).ConfigureAwait(false);
            if (relogged)
                pages = await src.GetPagesAsync(chapters[chapterIndex], token).ConfigureAwait(false);
        }

        if (pages != null)
            _cache.Set(key, pages, CacheTtl);
        return pages;
    }

    // ── Library streaming (read a not-yet-downloaded chapter live) ──────────
    // Same live-from-source mechanism as preview, but resolved from a library
    // series + chapter number instead of a Browse item. Lets the reader open
    // chapters that are still downloading or haven't been downloaded at all.

    // Paid/locked chapters surface as an exception at page-fetch time (the chapter
    // API has no lock field): "requires purchase", "log in via webview and
    // purchased this chapter to read", etc. Matched so genuine transient errors
    // (e.g. "Timed out waiting for page list") are NOT treated as locked.
    private static readonly Regex PurchaseError = new(
        @"(requires?\s+purchase|must\s+purchase|purchased?\s+this\s+chapter|log\s*in\s+via\s+webview|unlock\s+to\s+read|coins?\s+to\s+read|premium\s+chapter|chapter\s+locked|coins?\s+required)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static bool IsPurchaseError(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
            if (!string.IsNullOrEmpty(e.Message) && PurchaseError.IsMatch(e.Message))
                return true;
        return false;
    }

    public async Task<PreviewPagesDto?> GetLibraryStreamPagesAsync(Guid seriesId, decimal chapterNumber, Guid? userId = null, bool forceRefresh = false, bool refreshChapterList = false, CancellationToken token = default)
    {
        List<Page>? pages = await GetLibraryPageListAsync(seriesId, chapterNumber, userId, forceRefresh, refreshChapterList, token).ConfigureAwait(false);
        if (pages == null)
            return null;
        // Zero pages means the source withheld them — a paid/locked chapter.
        return new PreviewPagesDto { PageCount = pages.Count, Locked = pages.Count == 0 };
    }

    public async Task<(Stream? stream, string contentType)> GetLibraryStreamPageImageAsync(Guid seriesId, decimal chapterNumber, int pageIndex, Guid? userId = null, CancellationToken token = default)
    {
        // Served from RAM on the way back / re-scroll — no source round-trip.
        string imgKey = $"lib:img:{seriesId}:{chapterNumber}:{pageIndex}";
        if (_imageCache.TryGet(imgKey, out StreamImageCache.Entry cached))
            return (new MemoryStream(cached.Bytes, writable: false), cached.ContentType);

        List<Page>? pages = await GetLibraryPageListAsync(seriesId, chapterNumber, userId, false, false, token).ConfigureAwait(false);
        if (pages == null || pageIndex < 0 || pageIndex >= pages.Count)
            return (null, "");

        // Use the source that produced the cached page list, so page indices match.
        var resolved = await ResolveWinningProviderAsync(seriesId, chapterNumber, token).ConfigureAwait(false);
        if (resolved == null)
            return (null, "");

        ContentTypeStream? img;
        try
        {
            img = await SourceTimeout
                .RunAsync(c => resolved.Value.src.GetPageImageAsync(pages[pageIndex], c), StreamImageTimeout, token)
                .ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Stream page image timed out for series {SeriesId} ch {Chapter} page {Page}", seriesId, chapterNumber, pageIndex);
            return (null, "");
        }
        if (img == null)
            return (null, "");

        string ct = string.IsNullOrEmpty(img.ContentType) ? "image/jpeg" : img.ContentType;
        byte[] bytes = img.ToArray();
        _imageCache.Set(imgKey, bytes, ct);
        return (new MemoryStream(bytes, writable: false), ct);
    }

    /// <summary>
    /// Fetches page 0's image from a candidate source to confirm its image host is
    /// alive (a source can serve a page LIST but hang on the images). Caches the
    /// image so the first page then loads instantly. Returns false on timeout /
    /// failure so the caller falls through to the next source in priority order.
    /// </summary>
    private async Task<bool> ProbeAndCacheFirstImageAsync(
        Guid seriesId, decimal chapterNumber, ISourceInterop src, List<Page> pages, CancellationToken token)
    {
        if (pages.Count == 0)
            return false;
        string imgKey = $"lib:img:{seriesId}:{chapterNumber}:0";
        if (_imageCache.TryGet(imgKey, out _))
            return true; // page 0 already cached from a good source
        try
        {
            ContentTypeStream? img = await SourceTimeout
                .RunAsync(c => src.GetPageImageAsync(pages[0], c), StreamProbeTimeout, token)
                .ConfigureAwait(false);
            if (img == null)
                return false;
            string ct = string.IsNullOrEmpty(img.ContentType) ? "image/jpeg" : img.ContentType;
            _imageCache.Set(imgKey, img.ToArray(), ct);
            return true;
        }
        catch (TimeoutException) { return false; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Stream: image probe errored for ch {Chapter}", chapterNumber);
            return false;
        }
    }

    /// <summary>
    /// Every capable, permanent source that carries this chapter, ENABLED ones first
    /// and in the user's priority order. Streaming tries them in order so a
    /// failing/locked/slow source falls through to the next instead of failing the read.
    /// </summary>
    private async Task<List<SeriesProviderEntity>> GetCapableProvidersAsync(Guid seriesId, decimal chapterNumber, CancellationToken token)
    {
        SeriesEntity? series = await _db.Series.Include(s => s.Sources).AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        if (series == null)
            return new List<SeriesProviderEntity>();

        // "Capable" = technically usable for streaming (a resolvable remote source
        // carrying this chapter). A user-disabled source is NOT excluded here — it's
        // still tried, just ranked last, so a dead active source falls through to
        // whatever can actually serve the chapter.
        bool Capable(SeriesProviderEntity p) =>
            !p.IsUnknown && !p.IsLocal && !p.IsUninstalled && !string.IsNullOrEmpty(p.MihonProviderId);
        bool HasChapter(SeriesProviderEntity p) => p.Chapters.Any(c => !c.IsDeleted && c.Number == chapterNumber);

        // ENABLED first, then the user's per-series Priority (0 = highest), then storage.
        //
        // Enabled-ness is the primary key, not a tie-breaker. It used to sort third, behind
        // Priority, which meant a DISABLED source sitting at priority 0 was tried before every
        // enabled source below it — so the reader streamed from a source the user had switched
        // off, and only fell through to the green ones once it failed. Turning a source off in
        // the UI has to mean the reader stops reaching for it first.
        //
        // Disabled sources stay in the list, ranked last: they are the final fallback when no
        // enabled source can serve the chapter, which beats failing the read outright.
        //
        // Streaming walks this order and commits to the first source that serves the pages AND
        // their images, so one that times out or whose image host is dead steps down on its own.
        return series.Sources
            .Where(p => Capable(p) && HasChapter(p))
            .OrderByDescending(p => !p.IsDisabled)
            .ThenBy(p => p.Priority)
            .ThenByDescending(p => p.IsStorage)
            .ToList();
    }

    /// <summary>Resolves one specific source to its live chapter object.</summary>
    private async Task<(SeriesProviderEntity provider, ISourceInterop src, ParsedChapter chapter)?> ResolveProviderChapterAsync(
        SeriesProviderEntity target, decimal chapterNumber, bool forceRefresh, CancellationToken token)
    {
        ISourceInterop src;
        try { src = await _mihon.SourceFromProviderIdAsync(target.MihonProviderId!, token).ConfigureAwait(false); }
        catch (Exception e) { _logger.LogWarning(e, "Stream: could not resolve source for {Provider}", target.Provider); return null; }
        if (src == null)
            return null;

        // Re-asking the source for its whole CHAPTER LIST is a separate, far more
        // expensive thing than re-fetching one chapter's pages, and the two must
        // not share a flag.
        //
        // This call is bounded at StreamSourceTimeout (20s) and runs once per
        // candidate source, so a slow or wedged source costs 20s of the reader's
        // open before it steps down to the next one. Tying it to the ordinary
        // page refresh meant every open of an undownloaded chapter paid that —
        // and bought nothing, because the listing is only used to map a chapter
        // number to its URL, which does not change, and a chapter missing from
        // the listing already falls through to the DB reconstruction below.
        //
        // The one case that genuinely needs a fresh listing is the locked-chapter
        // poll: a coin-gated chapter is absent from the listing until it is
        // owned, so "has it appeared yet" is exactly a question about the list.
        string cacheKey = $"lib:ch:{target.Id}";
        if (forceRefresh)
            _cache.Remove(cacheKey);

        List<ParsedChapter>? chapters = _cache.Get<List<ParsedChapter>>(cacheKey);
        if (chapters == null)
        {
            Manga? manga = target.ToManga();
            if (manga == null)
                return null;
            try { chapters = await SourceTimeout.RunAsync(c => src.GetChaptersAsync(manga, c), StreamSourceTimeout, token).ConfigureAwait(false); }
            catch (Exception ex) { _logger.LogWarning(ex, "Stream: source {Provider} failed/timed out fetching chapter list", target.Provider); return null; }
            if (chapters == null)
                return null;
            chapters.ForEach(a => { if (string.IsNullOrEmpty(a.Scanlator)) a.Scanlator = target.Provider; });
            _cache.Set(cacheKey, chapters, CacheTtl);
        }

        // Apply the same scanlator scoping the download path uses.
        IEnumerable<ParsedChapter> pool = chapters;
        if (target.Scanlator == target.Provider || string.IsNullOrEmpty(target.Scanlator))
            pool = pool.Where(a => string.IsNullOrEmpty(a.Scanlator) || a.Scanlator == target.Provider);
        else
            pool = pool.Where(a => a.Scanlator == target.Scanlator);

        ParsedChapter? match = pool.FirstOrDefault(c => c.ParsedNumber == chapterNumber);
        if (match != null)
            return (target, src, match);

        // Not in the source's live chapter listing — this is the coin-gated-chapter
        // case LockedChapterSupplementService documents: sites running the WordPress
        // "lock chapters" plugin render a locked chapter with no <a href>, so the
        // extension's GetChaptersAsync silently drops it and can never match it here,
        // regardless of whether the user's site login actually owns it now. Fall back
        // to the DB's stored chapter (Number + Url, scraped once and persisted) and
        // hand its URL straight to GetPagesAsync — the same reconstruction
        // SeriesCommandService.ChapterToParsedChapter already uses for downloads, just
        // applied to the instant-read path too so a purchased chapter doesn't have to
        // wait for the next queued download to become readable.
        Models.Chapter? stored = target.Chapters.FirstOrDefault(c => c.Number == chapterNumber && !string.IsNullOrEmpty(c.Url));
        if (stored == null)
            return null;

        var reconstructed = new ParsedChapter
        {
            // Chapter.Url in the DB is the ABSOLUTE purchase/source link (see
            // SeriesCommandService's "backfill the purchase/source link" comment) —
            // but ParsedChapter.Url becomes SChapter.url, which HttpSource extensions
            // treat as a path RELATIVE to their own baseUrl (ConversionsExtensions:
            // RealUrl comes from source.getChapterUrl(chapter), a *different*,
            // already-absolutized value). Handing the absolute URL to both fields
            // made GetPagesAsync request baseUrl+absoluteUrl — a malformed, always-
            // failing double-domain URL — which is why purchased/unlocked chapters
            // never actually loaded even with a valid site login.
            Url = ToRelativeUrl(stored.Url),
            RealUrl = stored.Url ?? string.Empty,
            Name = stored.Name ?? string.Empty,
            ParsedName = stored.Name ?? string.Empty,
            ChapterNumber = (float)chapterNumber,
            ParsedNumber = chapterNumber,
            Index = stored.ProviderIndex,
            Scanlator = string.IsNullOrEmpty(target.Scanlator) ? target.Provider : target.Scanlator,
            DateUpload = stored.ProviderUploadDate.HasValue
                ? new DateTimeOffset(DateTime.SpecifyKind(stored.ProviderUploadDate.Value, DateTimeKind.Utc))
                : DateTimeOffset.UtcNow,
        };
        return (target, src, reconstructed);
    }

    /// <summary>Strips scheme+host off an absolute URL, leaving the path (+query/fragment)
    /// an HttpSource extension expects for SChapter.url. Returns the input unchanged if
    /// it isn't a valid absolute URL (already relative, or malformed).</summary>
    private static string ToRelativeUrl(string? absoluteUrl)
    {
        if (string.IsNullOrEmpty(absoluteUrl))
            return string.Empty;
        return Uri.TryCreate(absoluteUrl, UriKind.Absolute, out Uri? u) ? u.PathAndQuery + u.Fragment : absoluteUrl;
    }

    /// <summary>Resolves the source that served the cached page list (for image fetches).</summary>
    private async Task<(SeriesProviderEntity provider, ISourceInterop src, ParsedChapter chapter)?> ResolveWinningProviderAsync(
        Guid seriesId, decimal chapterNumber, CancellationToken token)
    {
        List<SeriesProviderEntity> providers = await GetCapableProvidersAsync(seriesId, chapterNumber, token).ConfigureAwait(false);
        if (providers.Count == 0)
            return null;

        Guid winnerId = _cache.TryGetValue($"lib:win:{seriesId}:{chapterNumber}", out Guid w) ? w : Guid.Empty;
        SeriesProviderEntity target = (winnerId != Guid.Empty ? providers.FirstOrDefault(p => p.Id == winnerId) : null) ?? providers.First();
        return await ResolveProviderChapterAsync(target, chapterNumber, false, token).ConfigureAwait(false);
    }

    /// <summary>
    /// One source's page-list attempt: timeout-bounded, with a re-login retry for
    /// coin sources whose session lapsed. Returns null on failure/timeout, an empty
    /// list when the source withholds the pages (locked), or the pages.
    /// </summary>
    private async Task<List<Page>?> TryGetPagesFromSourceAsync(
        ISourceInterop src, ParsedChapter chapter, SeriesProviderEntity provider, Guid? userId, CancellationToken token)
    {
        async Task<List<Page>?> Fetch()
        {
            try
            {
                return await SourceTimeout.RunAsync(c => src.GetPagesAsync(chapter, c), StreamSourceTimeout, token).ConfigureAwait(false);
            }
            catch (TimeoutException) { _logger.LogWarning("Stream: source {Provider} timed out fetching pages for ch {Chapter}", provider.Provider, chapter.ParsedNumber); return null; }
            catch (Exception ex) when (IsPurchaseError(ex)) { return new List<Page>(); }  // locked on this source
            catch (Exception ex) { _logger.LogWarning(ex, "Stream: source {Provider} failed page list for ch {Chapter}", provider.Provider, chapter.ParsedNumber); return null; }
        }

        List<Page>? pages = await Fetch().ConfigureAwait(false);
        if ((pages == null || pages.Count == 0) && userId != null)
        {
            bool relogged = await RateLimitedReloginAsync(userId.Value, provider.Provider, token).ConfigureAwait(false);
            if (relogged)
                pages = await Fetch().ConfigureAwait(false);
        }

        // Last resort for a chapter the user has already bought: on vcomics/Astro
        // sites the paid pages are NEVER in the HTML the extension scrapes — not
        // even for the purchaser — so the extension can only ever say "locked".
        // Ask the same authenticated endpoint the site's own reader uses. Returns
        // null unless the session actually owns the chapter, so this can't hand
        // back pages for something unpaid.
        if (pages == null || pages.Count == 0)
        {
            List<Page>? owned = await _vcomics
                .TryGetPurchasedPagesAsync(AbsoluteChapterUrl(chapter, provider), token).ConfigureAwait(false);
            if (owned is { Count: > 0 })
                return owned;
        }
        return pages;
    }

    /// <summary>
    /// Best-effort absolute URL for a chapter: the source reports RealUrl absolute,
    /// but fall back to resolving the (relative) chapter URL against the series URL.
    /// </summary>
    private static string? AbsoluteChapterUrl(ParsedChapter chapter, SeriesProviderEntity provider)
    {
        if (!string.IsNullOrWhiteSpace(chapter.RealUrl) &&
            Uri.TryCreate(chapter.RealUrl, UriKind.Absolute, out _))
            return chapter.RealUrl;

        if (!string.IsNullOrWhiteSpace(chapter.Url))
        {
            if (Uri.TryCreate(chapter.Url, UriKind.Absolute, out _))
                return chapter.Url;
            if (!string.IsNullOrWhiteSpace(provider.Url) &&
                Uri.TryCreate(provider.Url, UriKind.Absolute, out Uri? seriesUri) &&
                Uri.TryCreate(seriesUri, chapter.Url, out Uri? combined))
                return combined.ToString();
        }
        return null;
    }

    /// <summary>
    /// Clears Chapter.IsLocked once a live fetch actually returns pages for it.
    /// The chapters used to resolve/fetch above come from an AsNoTracking() query
    /// (GetCapableProvidersAsync), so this re-fetches the provider tracked and
    /// mutates the real entity — same "reload before mutating" requirement as the
    /// user detached-entity fixes elsewhere in this codebase. No-op if the flag is
    /// already clear, so this doesn't add a write on every ordinary page load.
    /// </summary>
    private async Task ClearLockedFlagAsync(Guid providerId, decimal chapterNumber, CancellationToken token)
    {
        try
        {
            SeriesProviderEntity? provider = await _db.SeriesProviders.FirstOrDefaultAsync(p => p.Id == providerId, token).ConfigureAwait(false);
            Models.Chapter? cha = provider?.Chapters.FirstOrDefault(c => c.Number == chapterNumber);
            if (cha == null || !cha.IsLocked)
                return;
            cha.IsLocked = false;
            _db.Touch(provider!, p => p.Chapters);
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort — the read itself already succeeded above regardless.
            _logger.LogDebug(ex, "Failed to clear IsLocked for provider {ProviderId} ch {Chapter}", providerId, chapterNumber);
        }
    }

    private async Task<List<Page>?> GetLibraryPageListAsync(Guid seriesId, decimal chapterNumber, Guid? userId, bool forceRefresh, bool refreshChapterList, CancellationToken token)
    {
        string key = $"lib:pg:{seriesId}:{chapterNumber}";
        // Force refresh skips the cached (often empty) page list so a chapter that
        // just got purchased / turned free is actually re-fetched from the source.
        List<Page>? stale = null;
        Guid staleWinner = Guid.Empty;
        if (forceRefresh)
        {
            // Keep the old list in hand rather than only deleting it: the cached
            // page IMAGES are keyed by page index, so whether they are still
            // correct is a question about how the NEW list compares to this one.
            // That comparison happens after the fetch, below.
            stale = _cache.Get<List<Page>>(key);
            if (_cache.TryGetValue($"lib:win:{seriesId}:{chapterNumber}", out Guid sw))
                staleWinner = sw;
            _cache.Remove(key);
        }
        else
        {
            List<Page>? cached = _cache.Get<List<Page>>(key);
            if (cached != null)
                return cached;
        }

        List<SeriesProviderEntity> providers = await GetCapableProvidersAsync(seriesId, chapterNumber, token).ConfigureAwait(false);
        if (providers.Count == 0)
            return null;

        // Try each permanent source in turn; the first that actually serves pages
        // wins. A source that times out, errors, or withholds a paid chapter falls
        // through to the next — so one bad/locked source no longer breaks the read.
        List<Page>? pages = null;
        Guid? winner = null;
        bool anyResolved = false;
        // First source that served a page list even if its images didn't probe OK —
        // a last resort so a single-source read is never worse than before.
        List<Page>? servedPages = null;
        Guid? servedWinner = null;

        foreach (SeriesProviderEntity provider in providers)
        {
            token.ThrowIfCancellationRequested();
            var resolved = await ResolveProviderChapterAsync(provider, chapterNumber, refreshChapterList, token).ConfigureAwait(false);
            if (resolved == null)
                continue;
            anyResolved = true;
            (_, ISourceInterop src, ParsedChapter chapter) = resolved.Value;

            List<Page>? attempt = await TryGetPagesFromSourceAsync(src, chapter, provider, userId, token).ConfigureAwait(false);
            if (attempt == null || attempt.Count == 0)
                continue; // empty (locked) or failed on this source — try the next one

            if (servedPages == null) { servedPages = attempt; servedWinner = provider.Id; }

            // A source can return a page LIST but have a dead/timing-out image host,
            // which makes every page time out. Probe page 0's image and only commit
            // to a source that can actually serve images — otherwise fall through to
            // the next one in priority order. The probe caches page 0.
            if (await ProbeAndCacheFirstImageAsync(seriesId, chapterNumber, src, attempt, token).ConfigureAwait(false))
            {
                pages = attempt;
                winner = provider.Id;
                // Pages actually came back — whatever the DB's stale IsLocked flag
                // says, this chapter is reachable now. Persist that so the chapter
                // list (which reads IsLocked, not this live result) stops showing
                // "Locked" for something the reader just proved it can open.
                await ClearLockedFlagAsync(provider.Id, chapterNumber, token).ConfigureAwait(false);
                break;
            }
            _logger.LogWarning("Stream: source {Provider} served a page list but its images wouldn't load for ch {Chapter}; trying the next source", provider.Provider, chapterNumber);
        }

        // No source served serviceable images → fall back to the first that at least
        // returned a page list (the client will retry the images), so a lone-source
        // read is never worse than it was before.
        if (pages == null && servedPages != null)
        {
            pages = servedPages;
            winner = servedWinner;
        }

        // No source could even resolve the chapter → genuinely not found.
        if (!anyResolved)
            return null;

        // A source resolved but none served pages → withheld/locked everywhere.
        pages ??= new List<Page>();

        // Remember which source served the pages so image fetches use the same one.
        if (winner != null)
            _cache.Set($"lib:win:{seriesId}:{chapterNumber}", winner.Value, CacheTtl);

        // Drop the cached page images ONLY if this refresh actually changed
        // something. They are keyed by page index, so they go stale exactly when
        // the list they were indexed against changes — a source adding pages, or
        // a different source winning, makes index N mean a different image.
        //
        // Evicting unconditionally is what made a reopen unusable: every page had
        // to come back from the source, and a slow one (Arena Scans) simply timed
        // out, so the reader served NotFound for page after page. An unchanged
        // chapter must reopen from RAM, which is also what makes the force-refresh
        // cheap enough to do on every open.
        //
        // Nothing is dropped when there was no previous list to compare against:
        // guessing "maybe stale" there costs a full re-fetch to protect a mismatch
        // that needs the page count to have changed to occur at all.
        if (forceRefresh && stale != null)
        {
            static string Identity(Page p) => p.ImageUrl ?? p.Url ?? string.Empty;
            bool changed =
                stale.Count != pages.Count
                || (winner != null && staleWinner != Guid.Empty && winner.Value != staleWinner)
                || !stale.Select(Identity).SequenceEqual(pages.Select(Identity));

            if (changed)
            {
                int sweep = Math.Max(stale.Count, pages.Count);
                for (int i = 0; i < sweep; i++)
                    _imageCache.Remove($"lib:img:{seriesId}:{chapterNumber}:{i}");
                _logger.LogInformation(
                    "Stream: chapter {Chapter} of series {SeriesId} changed on refresh ({Old} pages -> {New}); dropped its cached images.",
                    chapterNumber, seriesId, stale.Count, pages.Count);
            }
        }

        // Cache real results; never poison the cache with an empty list during a
        // forced poll, so the next 3s tick re-checks instead of returning 0.
        if (pages.Count > 0 || !forceRefresh)
            _cache.Set(key, pages, CacheTtl);
        return pages;
    }
}
