using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.ReadState;
using RenzoBackend.Services.Search;
using RenzoBackend.Services.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models.Extensions;
using System.Net;
using System.Text.Json;

namespace RenzoBackend.Services.Series
{
    /// <summary>
    /// Service responsible for querying series data
    /// </summary>
    public class SeriesQueryService
    {
        private readonly AppDbContext _db;
        private readonly SettingsService _settings;
        private readonly ProviderCacheService _providerCache;
        private readonly IMemoryCache _memoryCache;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ReadStateService _readState;
        private readonly ILogger<SeriesQueryService> _logger;

        public SeriesQueryService(AppDbContext db, SettingsService settings, ProviderCacheService providerCache,
            IMemoryCache memoryCache, IServiceScopeFactory scopeFactory, ReadStateService readState, ILogger<SeriesQueryService> logger)
        {
            _db = db;
            _settings = settings;
            _providerCache = providerCache;
            _memoryCache = memoryCache;
            _scopeFactory = scopeFactory;
            _readState = readState;
            _logger = logger;
        }

        // Upper bound on rows scanned when applying a genre filter client-side.
        // Genre is a value-converted CSV column EF can't translate a Contains/All
        // predicate over, so we stream FetchDate-desc rows and filter in memory up
        // to this cap to keep an unfiltered-heavy table from being fully walked.
        private const int MaxGenreScanRows = 20_000;

        /// <summary>
        /// True when a series belonging to <paramref name="seriesOwnerId"/> may be
        /// accessed by <paramref name="requesterId"/>: the owner themselves, an
        /// unowned legacy row (pre-migration, should only exist transiently), or
        /// an Owner-level requester with <paramref name="allowAll"/> set.
        /// </summary>
        public static bool CanAccessSeries(Guid seriesOwnerId, Guid requesterId, bool allowAll) =>
            seriesOwnerId == requesterId || seriesOwnerId == Guid.Empty || allowAll;

        /// <summary>
        /// Gets detailed information about a series by its unique identifier
        /// </summary>
        /// <param name="uid">The unique identifier of the series</param>
        /// <param name="requesterId">The requesting user's id — access is denied to series owned by someone else.</param>
        /// <param name="allowAll">True for an Owner-level requester viewing every library.</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Extended information about the series, or null if it doesn't exist or isn't accessible.</returns>
        public async Task<SeriesExtendedDto?> GetSeriesAsync(Guid uid, Guid requesterId, bool allowAll, CancellationToken token = default)
        {
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            Models.Database.SeriesEntity? s = await _db.Series
                .Include(a => a.Sources)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == uid, token);
            if (s == null || !CanAccessSeries(s.OwnerId, requesterId, allowAll))
                return null;
            return s.ToSeriesExtendedInfo(settings);
        }

        /// <summary>
        /// Gets the unified, series-level chapter list (merged across every source). For each
        /// chapter it reports whether a file is on disk and which source holds it, versus genuinely
        /// missing, plus the sources available for (re-)download. DB-only — no provider network call.
        /// </summary>
        /// <param name="seriesId">The unique identifier of the series.</param>
        /// <param name="requesterId">The requesting user's id.</param>
        /// <param name="allowAll">True for an Owner-level requester viewing every library.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task<List<ChapterDetailDto>?> GetSeriesChaptersAsync(Guid seriesId, Guid requesterId, bool allowAll, CancellationToken token = default)
        {
            Models.Database.SeriesEntity? s = await _db.Series
                .Include(a => a.Sources)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == seriesId, token).ConfigureAwait(false);
            if (s == null || !CanAccessSeries(s.OwnerId, requesterId, allowAll))
                return null;
            return s.ToChapterDetailList();
        }
        /*
        /// <summary>
        /// Gets the thumbnail for a series (moved from SeriesResourceService)
        /// </summary>
        public async Task<IActionResult> GetSeriesThumbnailAsync(string id, CancellationToken token = default)
        {
            var ret = await _etagCacheService.ETagWrapperAsync(id, async () =>
            {
                return await _thumbnailService.GetThumbnailAsync(id, token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);

            if (ret is StatusCodeResult r)
            {
                if (r.StatusCode == (int)HttpStatusCode.NotFound)
                {
                    return new FileStreamResult(
                        FileSystemExtensions.StreamEmbeddedResource("na.jpg") ?? new MemoryStream(), "image/jpeg");
                }
            }

            return ret;
        }
        */
        /// <summary>
        /// Gets the user's library of series
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <returns>List of series in the library</returns>
        /// <param name="requesterId">The requesting user's id — only their own series are returned.</param>
        /// <param name="allowAll">True for an Owner-level requester viewing every library (requester's own series still list first isn't guaranteed; callers wanting a specific user's library should pass that user's id as requesterId instead).</param>
        public async Task<List<SeriesInfoDto>> GetLibraryAsync(Guid requesterId, bool allowAll, CancellationToken token = default)
        {
            IQueryable<Models.Database.SeriesEntity> query = _db.Series.Include(s => s.Sources).AsNoTracking();
            if (!allowAll)
                query = query.Where(s => s.OwnerId == requesterId || s.OwnerId == Guid.Empty);
            List<Models.Database.SeriesEntity> series = await query.ToListAsync(token);
            return series.Select(a => a.ToSeriesInfo(_settings.DirectSettings)).ToList();
        }

        /// <summary>
        /// Builds the "Updates" feed (Suwayomi-style): chapters across the library
        /// ordered by when the update scan first FOUND them
        /// (<see cref="Models.Chapter.DateFetched"/>) — not the source's publish
        /// date and not the download time. The periodic library-update job stamps
        /// that as it discovers new chapters, so the newest finds float to the top.
        /// Chapters recorded before found-time tracking fall back to a stable
        /// historical date (publish date, else download date). One series-added
        /// entry per library is also included, keyed on DateAdded.
        /// </summary>
        /// <param name="start">Starting index for pagination</param>
        /// <param name="count">Number of items to return</param>
        /// <param name="requesterId">The requesting user's id — only their own series contribute.</param>
        /// <param name="allowAll">True for an Owner-level requester viewing every library.</param>
        /// <param name="token">Cancellation token</param>
        public async Task<List<UpdateFeedItemDto>> GetUpdatesFeedAsync(int start, int count, Guid requesterId, bool allowAll, CancellationToken token = default)
        {
            IQueryable<Models.Database.SeriesEntity> seriesQuery = _db.Series.Include(s => s.Sources).AsNoTracking();
            if (!allowAll)
                seriesQuery = seriesQuery.Where(s => s.OwnerId == requesterId || s.OwnerId == Guid.Empty);
            List<Models.Database.SeriesEntity> series = await seriesQuery.ToListAsync(token).ConfigureAwait(false);

            // Cap per-series so a huge back-catalogue can't dominate the merge; the
            // global feed only surfaces the most recent releases anyway.
            const int perSeriesCap = 300;

            // Suwayomi-style "found" time: when the update scan first discovered a
            // chapter (DateFetched). Chapters recorded before that was tracked fall
            // back to a stable historical date (real publish date, else download
            // date) so they sort sensibly instead of flooding the top.
            static DateTime? FoundTime(Models.Chapter c) =>
                c.DateFetched
                ?? (Extensions.ModelExtensions.HasRealUploadDate(c.ProviderUploadDate) ? c.ProviderUploadDate : null)
                ?? c.DownloadDate;

            List<UpdateFeedItemDto> items = new();
            foreach (Models.Database.SeriesEntity s in series)
            {
                // One entry per chapter number. When several sources carry the same
                // chapter, use the EARLIEST time it was found on any of them, so
                // adding a new source doesn't resurface a whole back-catalogue.
                var chapterEvents = s.Sources
                    .SelectMany(p => p.Chapters, (p, c) => (Provider: p, Chapter: c, Found: FoundTime(c)))
                    .Where(x => !x.Chapter.IsDeleted && x.Chapter.Number != null && x.Found != null)
                    .GroupBy(x => x.Chapter.Number)
                    .Select(g => g.OrderBy(x => x.Found).First()) // earliest-found row for this chapter
                    .OrderByDescending(x => x.Found)
                    .Take(perSeriesCap)
                    .ToList();

                foreach ((SeriesProviderEntity p, Models.Chapter c, DateTime? found) in chapterEvents)
                {
                    items.Add(new UpdateFeedItemDto
                    {
                        SeriesId = s.Id,
                        SeriesTitle = s.Title,
                        ThumbnailUrl = s.ThumbnailUrl,
                        Kind = UpdateFeedItemDto.KindNewChapter,
                        ChapterNumber = c.Number,
                        ChapterName = c.Name,
                        Provider = p.Provider,
                        Timestamp = found!.Value
                    });
                }

                if (s.DateAdded != null)
                {
                    items.Add(new UpdateFeedItemDto
                    {
                        SeriesId = s.Id,
                        SeriesTitle = s.Title,
                        ThumbnailUrl = s.ThumbnailUrl,
                        Kind = UpdateFeedItemDto.KindSeriesAdded,
                        Timestamp = s.DateAdded.Value
                    });
                }
            }

            List<UpdateFeedItemDto> page = items
                .OrderByDescending(i => i.Timestamp)
                .Skip(start)
                .Take(count)
                .ToList();

            // Flag chapters the requester has already finished so the UI can grey
            // them out. Resolve read state only for the series on this page (each
            // series' state is a cached renzo.json read), and never let a read-state
            // hiccup break the feed itself.
            try
            {
                string? username = await _db.Users
                    .Where(u => u.Id == requesterId)
                    .Select(u => u.Username)
                    .FirstOrDefaultAsync(token).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(username))
                {
                    Dictionary<Guid, string> storageById = series
                        .Where(s => !string.IsNullOrWhiteSpace(s.StoragePath))
                        .ToDictionary(s => s.Id, s => s.StoragePath);
                    Dictionary<Guid, HashSet<decimal>> completedBySeries = new();
                    foreach (UpdateFeedItemDto item in page)
                    {
                        if (item.Kind != UpdateFeedItemDto.KindNewChapter || item.ChapterNumber == null)
                            continue;
                        if (!completedBySeries.TryGetValue(item.SeriesId, out HashSet<decimal>? completed))
                        {
                            completed = new HashSet<decimal>();
                            if (storageById.TryGetValue(item.SeriesId, out string? storagePath))
                            {
                                foreach (Models.ReadState.ChapterReadState st in _readState.GetSeriesReadStates(username, storagePath))
                                    if (st.IsCompleted) completed.Add(st.ChapterNumber);
                            }
                            completedBySeries[item.SeriesId] = completed;
                        }
                        item.Read = completed.Contains(item.ChapterNumber.Value);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Updates feed: couldn't resolve read state; returning feed without read flags.");
            }

            return page;
        }

        /// <summary>
        /// Gets the latest series with optional filtering
        /// </summary>
        /// <param name="start">Starting index for pagination</param>
        /// <param name="count">Number of items to return</param>
        /// <param name="sourceid">Optional source ID filter</param>
        /// <param name="keyword">Optional keyword filter</param>
        /// <param name="genres">Optional tag/genre filter; a row must carry every supplied tag (AND semantics)</param>
        /// <param name="requesterId">The requesting user's id — the "in library" badge reflects only their own series.</param>
        /// <param name="allowAll">True for an Owner-level requester viewing every library (in-library badge then matches any owner).</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>List of latest series information</returns>
        public async Task<List<LatestSeriesDto>> GetLatestAsync(int start, int count, string? mihonProviderId,
            string? keyword, IReadOnlyList<string>? genres, Guid requesterId, bool allowAll, CancellationToken token = default)
        {
            // Keyword searches go to the full-catalog path: the cached feed only
            // contains series that appeared in a source's latest/popular listing,
            // so a plain LIKE over it misses everything older — instead we merge
            // the cached rows with a live search across the sources themselves.
            if (!string.IsNullOrWhiteSpace(keyword))
                return await GetKeywordCatalogPageAsync(start, count, mihonProviderId, keyword, genres, requesterId, allowAll, token).ConfigureAwait(false);

            // Single-source browse (no keyword): the cache for a given source only
            // holds whatever its last scheduled latest/popular job pulled — often a
            // page or two — so a freshly added or thin source shows almost nothing.
            // Merge the cache with a live multi-page catalog fetch of that source.
            if (!string.IsNullOrEmpty(mihonProviderId))
                return await GetSourceCatalogPageAsync(start, count, mihonProviderId, genres, requesterId, allowAll, token).ConfigureAwait(false);

            // All-sources, no keyword: serve the aggregate cache immediately, and
            // (on the first page only) kick off a background sweep that pulls every
            // enabled source's live catalog into the cache so the feed keeps filling
            // out. Non-blocking + rate-limited so it never hammers sources.
            if (start == 0)
                KickOffAllSourcesCatalogFill();

            HashSet<string>? enabledForBrowse = await GetEnabledProviderIdsOrNullAsync(requesterId, allowAll, token).ConfigureAwait(false);
            if (enabledForBrowse != null && enabledForBrowse.Count == 0)
                return []; // nothing enabled yet — an empty Browse, not "everything"

            IQueryable<LatestSerieEntity> series = _db.LatestSeries;
            if (!string.IsNullOrEmpty(mihonProviderId))
            {
                series = series.Where(a => a.MihonProviderId == mihonProviderId);
            }
            if (enabledForBrowse != null)
            {
                series = series.Where(a => a.MihonProviderId != null && enabledForBrowse.Contains(a.MihonProviderId));
            }

            series = series.OrderByDescending(a => a.FetchDate);

            // Normalize the incoming genre filter; null/empty (after trimming blanks)
            // means "no tag filter" and we take the fast SQL pagination path.
            List<string>? normalizedGenres = null;
            if (genres != null && genres.Count > 0)
            {
                normalizedGenres = genres
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => g.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (normalizedGenres.Count == 0)
                    normalizedGenres = null;
            }

            if (normalizedGenres == null)
            {
                if (start > 0)
                    series = series.Skip(start);

                List<LatestSeriesDto> page = (await series.Take(count).ToListAsync(token).ConfigureAwait(false))
                    .Select(a => a.ToSeriesInfo()).ToList();
                await PopulateOwnerLibraryStatusAsync(page, requesterId, allowAll, token).ConfigureAwait(false);
                await PopulateNsfwDetectionAsync(page, token).ConfigureAwait(false);
                return page;
            }

            // Genre filtering. Genre is stored as a value-converted CSV column
            // (List<string> ↔ string), so EF can't translate the predicate to SQL.
            // Stream rows in FetchDate-desc order, filter client-side with AND
            // semantics (a row must carry every selected tag), apply the offset
            // against matches, and stop once enough are produced or the scan cap hits.
            var taken = new List<LatestSerieEntity>(count);
            var rangeStart = Math.Max(0, start);
            int matched = 0;
            int scanned = 0;

            await foreach (var row in series.AsAsyncEnumerable().WithCancellation(token))
            {
                scanned++;
                if (scanned > MaxGenreScanRows)
                    break;

                if (row.Genre == null || row.Genre.Count == 0)
                    continue;

                var rowSet = new HashSet<string>(row.Genre.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var g in row.Genre)
                {
                    var trimmed = g?.Trim();
                    if (!string.IsNullOrEmpty(trimmed))
                        rowSet.Add(trimmed);
                }

                bool hasAll = true;
                foreach (var want in normalizedGenres)
                {
                    if (!rowSet.Contains(want))
                    {
                        hasAll = false;
                        break;
                    }
                }
                if (!hasAll)
                    continue;

                if (matched < rangeStart)
                {
                    matched++;
                    continue;
                }

                taken.Add(row);
                matched++;
                if (taken.Count >= count)
                    break;
            }

            List<LatestSeriesDto> result = taken.Select(a => a.ToSeriesInfo()).ToList();
            await PopulateOwnerLibraryStatusAsync(result, requesterId, allowAll, token).ConfigureAwait(false);
            await PopulateNsfwDetectionAsync(result, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// MihonProviderIds the given user has personally enabled, or null for "no
        /// filter" (Owner-level viewAll). A source only shows up in a user's Browse
        /// catalog if they've enabled it themselves — same rule as Search, and the
        /// only thing standing between a brand-new/restricted profile and every
        /// adult source another user has installed.
        /// </summary>
        private async Task<HashSet<string>?> GetEnabledProviderIdsOrNullAsync(Guid requesterId, bool allowAll, CancellationToken token)
        {
            if (allowAll)
                return null;
            List<string> ids = await _db.UserProviders
                .Where(p => p.UserId == requesterId)
                .Select(p => p.MihonProviderId)
                .ToListAsync(token).ConfigureAwait(false);
            return new HashSet<string>(ids, StringComparer.Ordinal);
        }

        /// <summary>
        /// Re-derives InLibrary/SeriesId per row against the REQUESTING user's own
        /// library, overriding whatever the shared LatestSeries cache carries (that
        /// cache is populated by a background job that matches ANY owner's
        /// SeriesProviders, so it can't be trusted to answer "is this in MY
        /// library"). Skipped when allowAll — an Owner viewing every library sees
        /// whichever owner's row the cache already resolved.
        /// </summary>
        private async Task PopulateOwnerLibraryStatusAsync(List<LatestSeriesDto> page, Guid requesterId, bool allowAll, CancellationToken token)
        {
            if (allowAll || page.Count == 0)
                return;

            var ids = page.Select(p => p.MihonId).Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            if (ids.Count == 0)
                return;

            var owned = await (
                from sp in _db.SeriesProviders
                join se in _db.Series on sp.SeriesId equals se.Id
                where sp.MihonId != null && ids.Contains(sp.MihonId) && se.OwnerId == requesterId
                select new { sp.MihonId, sp.SeriesId }
            ).ToListAsync(token).ConfigureAwait(false);
            var byMihonId = owned.GroupBy(o => o.MihonId!).ToDictionary(g => g.Key, g => g.First().SeriesId, StringComparer.Ordinal);

            foreach (LatestSeriesDto row in page)
            {
                if (byMihonId.TryGetValue(row.MihonId, out Guid sid))
                {
                    row.SeriesId = sid;
                    row.InLibrary = InLibraryStatus.InLibrary;
                }
                else
                {
                    row.SeriesId = null;
                    row.InLibrary = InLibraryStatus.NotInLibrary;
                }
            }
        }

        // How long a single browse request waits for the live source search
        // before returning cached rows only. The search keeps running in the
        // background and lands in the memory cache, so the next request (page
        // scroll, idle refresh, retyped keyword) picks the full set up.
        private const int LiveSearchBudgetMs = 15_000;

        /// <summary>
        /// Full-catalog browse search: merges the cached latest/popular feed with
        /// a live search across the actual sources, so titles that never appeared
        /// in a latest listing (old completed series, brand-new sources) are found.
        /// </summary>
        private async Task<List<LatestSeriesDto>> GetKeywordCatalogPageAsync(int start, int count,
            string? mihonProviderId, string keyword, IReadOnlyList<string>? genres, Guid requesterId, bool allowAll, CancellationToken token)
        {
            keyword = keyword.Trim();

            IQueryable<LatestSerieEntity> cachedQuery = _db.LatestSeries;
            if (!string.IsNullOrEmpty(mihonProviderId))
                cachedQuery = cachedQuery.Where(a => a.MihonProviderId == mihonProviderId);
            cachedQuery = cachedQuery
                .Where(a => EF.Functions.Like(a.Title, $"%{keyword}%"))
                .OrderByDescending(a => a.FetchDate);

            List<LatestSeriesDto> merged = (await cachedQuery.Take(500).ToListAsync(token).ConfigureAwait(false))
                .Select(a => a.ToSeriesInfo()).ToList();
            var known = new HashSet<string>(merged.Select(m => m.MihonId), StringComparer.Ordinal);

            List<LatestSeriesDto> live = await GetLiveSearchRowsAsync(keyword, mihonProviderId, token).ConfigureAwait(false);
            foreach (LatestSeriesDto row in live)
            {
                if (known.Add(row.MihonId))
                    merged.Add(row);
            }

            // In-library status is re-derived below (PopulateOwnerLibraryStatusAsync)
            // against the REQUESTING user's own library, for both cached and live rows.

            // Source visibility: a source only shows up in Browse if the requester
            // has personally enabled it — same rule Search already applies.
            HashSet<string>? enabledForBrowse = await GetEnabledProviderIdsOrNullAsync(requesterId, allowAll, token).ConfigureAwait(false);
            if (enabledForBrowse != null)
                merged = merged.Where(m => m.MihonProviderId != null && enabledForBrowse.Contains(m.MihonProviderId)).ToList();

            // Genre filter (AND semantics, same as the non-keyword path).
            List<string>? wantedGenres = genres?
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (wantedGenres is { Count: > 0 })
            {
                merged = merged.Where(row =>
                {
                    if (row.Genre == null || row.Genre.Count == 0)
                        return false;
                    var rowSet = new HashSet<string>(row.Genre.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
                    return wantedGenres.All(rowSet.Contains);
                }).ToList();
            }

            // Order by fuzzy relevance to the keyword, freshest first on ties.
            if (merged.Count > 0)
            {
                var scored = Scrobbling.TitleMatcher.MatchTitles(
                    originalTitles: new[] { keyword },
                    candidates: merged.Select(m => (m.Title, Id: m.MihonId)).ToList(),
                    minimumScore: 0);
                var scoreLookup = scored.ToDictionary(s => s.Id, s => s.Percentage, StringComparer.Ordinal);
                merged = merged
                    .OrderByDescending(m => scoreLookup.TryGetValue(m.MihonId, out var sc) ? sc : -1)
                    .ThenByDescending(m => m.FetchDate)
                    .ToList();
            }

            List<LatestSeriesDto> page = merged.Skip(Math.Max(0, start)).Take(count).ToList();
            await PopulateOwnerLibraryStatusAsync(page, requesterId, allowAll, token).ConfigureAwait(false);
            await PopulateNsfwDetectionAsync(page, token).ConfigureAwait(false);
            return page;
        }

        /// <summary>
        /// Awaits the (shared, cached) live source search up to <see cref="LiveSearchBudgetMs"/>.
        /// A slow fan-out returns empty for this request; the task keeps running
        /// and its result is served from cache to subsequent requests.
        /// </summary>
        private async Task<List<LatestSeriesDto>> GetLiveSearchRowsAsync(string keyword, string? mihonProviderId, CancellationToken token)
        {
            string cacheKey = $"BrowseLive:{mihonProviderId ?? "all"}:{keyword.ToLowerInvariant()}";
            Task<List<LatestSeriesDto>>? searchTask = _memoryCache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                // Own scope + Task.Run: the search must survive this HTTP request
                // ending (its result is cached for the next one), so it can't use
                // request-scoped services.
                return Task.Run(() => RunLiveSearchAsync(keyword, mihonProviderId));
            });
            if (searchTask == null)
                return [];

            Task finished = await Task.WhenAny(searchTask, Task.Delay(LiveSearchBudgetMs, token)).ConfigureAwait(false);
            if (finished != searchTask)
            {
                _logger.LogInformation("Live browse search for '{Keyword}' still running after {Budget}ms; returning cached rows for now.", keyword, LiveSearchBudgetMs);
                return [];
            }
            try
            {
                return await searchTask.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Live browse search for '{Keyword}' failed.", keyword);
                return [];
            }
        }

        // Pages of a source's popular/latest listing to pull on a live catalog
        // fetch, and the hard cap on rows kept. Enough to fill several screens of
        // infinite scroll without walking a source's entire catalogue.
        private const int LiveCatalogPages = 8;
        private const int LiveCatalogMaxRows = 400;

        /// <summary>
        /// Single-source Browse (no keyword): merges the source's cached rows with
        /// a live multi-page fetch of its latest/popular listing, so a thin or newly
        /// added source shows a full catalogue instead of just the last job's page.
        /// </summary>
        private async Task<List<LatestSeriesDto>> GetSourceCatalogPageAsync(int start, int count,
            string mihonProviderId, IReadOnlyList<string>? genres, Guid requesterId, bool allowAll, CancellationToken token)
        {
            List<LatestSeriesDto> merged = (await _db.LatestSeries
                    .Where(a => a.MihonProviderId == mihonProviderId)
                    .OrderByDescending(a => a.FetchDate)
                    .Take(LiveCatalogMaxRows)
                    .ToListAsync(token).ConfigureAwait(false))
                .Select(a => a.ToSeriesInfo()).ToList();
            var known = new HashSet<string>(merged.Select(m => m.MihonId), StringComparer.Ordinal);

            // A source that isn't enabled for this requester can't be browsed at
            // all — including by explicit sourceId — same as Search.
            HashSet<string>? enabledForBrowse0 = await GetEnabledProviderIdsOrNullAsync(requesterId, allowAll, token).ConfigureAwait(false);
            if (enabledForBrowse0 != null && !enabledForBrowse0.Contains(mihonProviderId))
                return [];

            List<LatestSeriesDto> live = await GetLiveCatalogRowsAsync(mihonProviderId, token).ConfigureAwait(false);
            foreach (LatestSeriesDto row in live)
            {
                if (known.Add(row.MihonId))
                    merged.Add(row);
            }

            // In-library status is re-derived below (PopulateOwnerLibraryStatusAsync)
            // against the REQUESTING user's own library, for both cached and live rows.

            List<string>? wantedGenres = genres?
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (wantedGenres is { Count: > 0 })
            {
                merged = merged.Where(row =>
                {
                    if (row.Genre == null || row.Genre.Count == 0)
                        return false;
                    var rowSet = new HashSet<string>(row.Genre.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
                    return wantedGenres.All(rowSet.Contains);
                }).ToList();
            }

            // Recently-updated first (real FetchDate from update jobs), then the
            // broader catalogue (seeded a year back in listing order) below.
            merged = merged.OrderByDescending(m => m.FetchDate).ToList();

            List<LatestSeriesDto> page = merged.Skip(Math.Max(0, start)).Take(count).ToList();
            await PopulateOwnerLibraryStatusAsync(page, requesterId, allowAll, token).ConfigureAwait(false);
            await PopulateNsfwDetectionAsync(page, token).ConfigureAwait(false);
            return page;
        }

        /// <summary>
        /// Awaits the (shared, cached) live catalog fetch for a source up to the
        /// live-search budget. A slow source returns empty for this request; the
        /// task keeps running and its result serves subsequent requests from cache.
        /// </summary>
        private async Task<List<LatestSeriesDto>> GetLiveCatalogRowsAsync(string mihonProviderId, CancellationToken token)
        {
            string cacheKey = $"BrowseCatalog:{mihonProviderId}";
            Task<List<LatestSeriesDto>>? task = _memoryCache.GetOrCreate(cacheKey, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(10);
                return Task.Run(() => RunLiveCatalogAsync(mihonProviderId));
            });
            if (task == null)
                return [];

            Task finished = await Task.WhenAny(task, Task.Delay(LiveSearchBudgetMs, token)).ConfigureAwait(false);
            if (finished != task)
            {
                _logger.LogInformation("Live catalog fetch for source {Source} still running after {Budget}ms; returning cached rows for now.", mihonProviderId, LiveSearchBudgetMs);
                return [];
            }
            try
            {
                return await task.ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Live catalog fetch for source {Source} failed.", mihonProviderId);
                return [];
            }
        }

        private async Task<List<LatestSeriesDto>> RunLiveCatalogAsync(string mihonProviderId)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            var mihon = scope.ServiceProvider.GetRequiredService<Bridge.MihonBridgeService>();
            var thumb = scope.ServiceProvider.GetRequiredService<Images.ThumbCacheService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            Mihon.ExtensionsBridge.Models.Abstractions.ISourceInterop src;
            try
            {
                src = await mihon.SourceFromProviderIdAsync(mihonProviderId).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Unable to resolve source {Source} for live catalog fetch.", mihonProviderId);
                return [];
            }

            bool useLatest = src.SupportsLatest;
            string lang = src.Language == "all" ? string.Empty : src.Language;
            var rows = new List<LatestSeriesDto>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            // Catalog rows carry no real per-series update time, so they must NOT
            // outrank series the update jobs stamped with genuine recency. Seed them
            // a year back, decremented by listing position, so the aggregate feed
            // shows truly-recently-updated series first and the broader catalogue
            // below — while preserving each source's own listing order within it.
            DateTime seededBase = DateTime.UtcNow.AddYears(-1);
            int order = 0;

            for (int page = 1; page <= LiveCatalogPages && rows.Count < LiveCatalogMaxRows; page++)
            {
                MangaList? res;
                try
                {
                    res = await (useLatest ? src.GetLatestAsync(page) : src.GetPopularAsync(page)).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Live catalog page {Page} failed for source {Source}.", page, mihonProviderId);
                    break;
                }
                if (res == null || res.Mangas == null || res.Mangas.Count == 0)
                    break;

                foreach (ParsedManga m in res.Mangas)
                {
                    if (string.IsNullOrEmpty(m.Url))
                        continue;
                    string mihonId = mihonProviderId + "|" + m.Url;
                    if (!seen.Add(mihonId))
                        continue;
                    if (!string.IsNullOrEmpty(m.ThumbnailUrl))
                        await thumb.AddUrlAsync(m.ThumbnailUrl, mihonProviderId).ConfigureAwait(false);
                    rows.Add(new LatestSeriesDto
                    {
                        MihonId = mihonId,
                        MihonProviderId = mihonProviderId,
                        Provider = src.Name,
                        Language = lang,
                        Title = m.Title,
                        ThumbnailUrl = m.ThumbnailUrl,
                        Url = m.Url,
                        Artist = m.Artist,
                        Author = m.Author,
                        Description = m.Description,
                        Genre = m.GetGenres(),
                        Status = (SeriesStatus)(int)m.Status,
                        FetchDate = seededBase.AddSeconds(-order++),
                    });
                    if (rows.Count >= LiveCatalogMaxRows)
                        break;
                }

                if (!res.HasNextPage)
                    break;
            }

            // Persist newly discovered rows into the shared LatestSeries cache so
            // the aggregate ("All Sources") Browse feed fills out too — not just the
            // single-source view that triggered this fetch. Only insert MihonIds we
            // don't already have; existing rows may carry richer job-populated data.
            if (rows.Count > 0)
            {
                try
                {
                    var ids = rows.Select(r => r.MihonId).ToList();
                    var existing = await db.LatestSeries
                        .Where(a => ids.Contains(a.MihonId))
                        .Select(a => a.MihonId)
                        .ToListAsync().ConfigureAwait(false);
                    var have = new HashSet<string>(existing, StringComparer.Ordinal);
                    foreach (LatestSeriesDto r in rows)
                    {
                        if (have.Contains(r.MihonId))
                            continue;
                        db.LatestSeries.Add(new LatestSerieEntity
                        {
                            MihonId = r.MihonId,
                            MihonProviderId = r.MihonProviderId,
                            Provider = r.Provider,
                            Language = r.Language,
                            Url = r.Url,
                            Title = r.Title,
                            ThumbnailUrl = r.ThumbnailUrl,
                            Artist = r.Artist,
                            Author = r.Author,
                            Description = r.Description,
                            Genre = r.Genre,
                            Status = r.Status,
                            FetchDate = r.FetchDate,
                        });
                    }
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to persist live catalog rows for {Source}.", mihonProviderId);
                }
            }

            _logger.LogInformation("Live catalog fetch for {Source} produced {Count} series ({Listing}).",
                mihonProviderId, rows.Count, useLatest ? "latest" : "popular");
            return rows;
        }

        // Gate so only one full all-sources catalog fan-out runs at a time. A fan-out
        // touches every enabled source's listing, so overlapping runs would multiply
        // outbound requests (ban risk) for no benefit — the per-source 10-min cache
        // already makes a second run a no-op anyway.
        private static readonly SemaphoreSlim _catalogFanoutGate = new(1, 1);
        private static DateTime _lastCatalogFanoutUtc = DateTime.MinValue;

        /// <summary>
        /// Background, concurrency-limited fill of the aggregate Browse feed: pulls
        /// each enabled source's live catalog (reusing the cached per-source task) and
        /// persists it. Fire-and-forget — the current request serves cache immediately
        /// and the next one sees the filled-in rows. Rate-limited to once every few
        /// minutes and capped concurrency to stay well clear of source ban thresholds.
        /// </summary>
        private void KickOffAllSourcesCatalogFill()
        {
            _ = Task.Run(async () =>
            {
                if (!await _catalogFanoutGate.WaitAsync(0).ConfigureAwait(false))
                    return; // one already running
                try
                {
                    // Throttle: a completed fan-out is good for a while (per-source
                    // caches are 10 min), so don't re-sweep on every browse open.
                    if (DateTime.UtcNow - _lastCatalogFanoutUtc < TimeSpan.FromMinutes(5))
                        return;

                    using IServiceScope scope = _scopeFactory.CreateScope();
                    var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
                    var providerCache = scope.ServiceProvider.GetRequiredService<ProviderCacheService>();

                    SettingsDto settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
                    List<string> languages = settings.PreferredLanguages.ToList();
                    if (languages.Count == 0)
                        languages = ["en"];
                    List<ProviderStorageEntity> sources = await providerCache.GetSourcesForLanguagesAsync(languages).ConfigureAwait(false);

                    _logger.LogInformation("Sweeping live catalog across {Count} sources to fill the aggregate Browse feed.", sources.Count);

                    // Cap concurrency low: this is a background enrichment, not a race.
                    using var concurrency = new SemaphoreSlim(4, 4);
                    var tasks = sources
                        .Where(s => !string.IsNullOrEmpty(s.MihonProviderId))
                        .Select(async s =>
                        {
                            await concurrency.WaitAsync().ConfigureAwait(false);
                            try { await GetLiveCatalogRowsAsync(s.MihonProviderId, CancellationToken.None).ConfigureAwait(false); }
                            catch (Exception e) { _logger.LogWarning(e, "Catalog sweep failed for {Source}.", s.MihonProviderId); }
                            finally { concurrency.Release(); }
                        })
                        .ToList();
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                    _lastCatalogFanoutUtc = DateTime.UtcNow;
                    _logger.LogInformation("Live catalog sweep complete.");
                }
                finally
                {
                    _catalogFanoutGate.Release();
                }
            });
        }

        private async Task<List<LatestSeriesDto>> RunLiveSearchAsync(string keyword, string? mihonProviderId)
        {
            using IServiceScope scope = _scopeFactory.CreateScope();
            SearchQueryService search = scope.ServiceProvider.GetRequiredService<SearchQueryService>();
            SettingsService settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
            ProviderCacheService providerCache = scope.ServiceProvider.GetRequiredService<ProviderCacheService>();

            SettingsDto settings = await settingsService.GetSettingsAsync().ConfigureAwait(false);
            List<string> languages = settings.PreferredLanguages.ToList();
            if (languages.Count == 0)
                languages = ["en"];

            List<ProviderStorageEntity> sources = await providerCache.GetSourcesForLanguagesAsync(languages).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(mihonProviderId))
                sources = sources.Where(s => s.MihonProviderId == mihonProviderId).ToList();
            if (sources.Count == 0)
                return [];

            List<LinkedSeriesDto> linked = await search.SearchSeriesAsync(keyword, sources, settings).ConfigureAwait(false);

            var rows = new List<LatestSeriesDto>(linked.Count);
            foreach (LinkedSeriesDto l in linked)
            {
                if (string.IsNullOrEmpty(l.MihonId))
                    continue;

                // BridgeItemInfo carries the raw parsed manga: url, description,
                // genres, status — everything the details modal renders.
                Manga? manga = null;
                if (!string.IsNullOrEmpty(l.BridgeItemInfo))
                {
                    try { manga = JsonSerializer.Deserialize<Manga>(l.BridgeItemInfo); }
                    catch { /* older/foreign payload — render from the summary fields */ }
                }

                int sep = l.MihonId.IndexOf('|');
                rows.Add(new LatestSeriesDto
                {
                    MihonId = l.MihonId,
                    MihonProviderId = l.MihonProviderId,
                    Provider = l.Provider,
                    Language = l.Lang,
                    Title = l.Title,
                    ThumbnailUrl = l.ThumbnailUrl ?? manga?.ThumbnailUrl,
                    Url = manga?.Url ?? (sep >= 0 ? l.MihonId[(sep + 1)..] : null),
                    Artist = manga?.Artist,
                    Author = manga?.Author,
                    Description = manga?.Description,
                    Genre = manga?.GetGenres() ?? [],
                    Status = manga != null ? (SeriesStatus)(int)manga.Status : SeriesStatus.UNKNOWN,
                    FetchDate = DateTime.UtcNow,
                });
            }
            return rows;
        }

        /// <summary>
        /// Fills <see cref="LatestSeriesDto.IsNsfw"/> for a page of Browse rows.
        /// Detection deliberately looks beyond the row's own tags — many sources
        /// don't expose adult ratings — by borrowing tags from (a) same-titled
        /// catalog rows fetched from OTHER sources and (b) the linked/same-titled
        /// library series, whose flag already aggregates every source's tags plus
        /// the user's manual 18+ override. Detection only: borrowed tags are never
        /// added to the row's visible genre list.
        /// </summary>
        private async Task PopulateNsfwDetectionAsync(List<LatestSeriesDto> page, CancellationToken token)
        {
            if (page.Count == 0)
                return;

            foreach (LatestSeriesDto row in page)
            {
                if (AdultContentClassifier.IsAdult(row.Genre))
                    row.IsNsfw = true;
            }

            List<LatestSeriesDto> pending = page.Where(r => !r.IsNsfw).ToList();
            if (pending.Count == 0)
                return;

            List<string> titles = pending
                .Select(r => r.Title.Trim().ToLowerInvariant())
                .Where(t => t.Length > 0)
                .Distinct()
                .ToList();
            List<Guid> linkedIds = pending
                .Where(r => r.SeriesId != null && r.SeriesId != Guid.Empty)
                .Select(r => r.SeriesId!.Value)
                .Distinct()
                .ToList();

            // Same-titled rows from other sources in the cached catalog.
            HashSet<string> adultTitles = new(StringComparer.OrdinalIgnoreCase);
            var catalogMatches = await _db.LatestSeries
                .Where(a => titles.Contains(a.Title.ToLower()))
                .Select(a => new { a.Title, a.Genre })
                .ToListAsync(token).ConfigureAwait(false);
            foreach (var m in catalogMatches)
            {
                if (AdultContentClassifier.IsAdult(m.Genre))
                    adultTitles.Add(m.Title.Trim());
            }

            // Linked or same-titled library series: manual override, series tags,
            // or any of its sources' tags.
            HashSet<Guid> adultSeriesIds = new();
            var libraryMatches = await _db.Series
                .Include(s => s.Sources)
                .Where(s => linkedIds.Contains(s.Id) || titles.Contains(s.Title.ToLower()))
                .ToListAsync(token).ConfigureAwait(false);
            foreach (var s in libraryMatches)
            {
                bool adult = s.Nsfw
                             || AdultContentClassifier.IsAdult(s.Genre)
                             || s.Sources.Any(src => AdultContentClassifier.IsAdult(src.Genre));
                if (!adult)
                    continue;
                adultSeriesIds.Add(s.Id);
                adultTitles.Add(s.Title.Trim());
            }

            foreach (LatestSeriesDto row in pending)
            {
                if ((row.SeriesId != null && adultSeriesIds.Contains(row.SeriesId.Value)) ||
                    adultTitles.Contains(row.Title.Trim()))
                {
                    row.IsNsfw = true;
                }
            }
        }

        /// <summary>
        /// Returns the distinct tags/genres present in the cached "Latest" cloud
        /// catalogue along with the number of series carrying each (most-used first,
        /// then alphabetical). Populates the browse-screen tag filter.
        /// </summary>
        /// <param name="token">Cancellation token</param>
        /// <returns>Distinct genres with their occurrence counts</returns>
        public async Task<List<LatestGenreDto>> GetLatestGenresAsync(CancellationToken token = default)
        {
            // Project only the Genre column so whole rows aren't materialized; EF
            // still applies the value converter, giving a List<string> per row.
            List<List<string>> genreLists = await _db.LatestSeries
                .AsNoTracking()
                .Select(a => a.Genre)
                .ToListAsync(token).ConfigureAwait(false);

            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var glist in genreLists)
            {
                if (glist == null)
                    continue;
                foreach (var raw in glist)
                {
                    var name = raw?.Trim();
                    if (string.IsNullOrEmpty(name))
                        continue;
                    counts.TryGetValue(name, out int c);
                    counts[name] = c + 1;
                }
            }

            return counts
                .Select(kv => new LatestGenreDto { Name = kv.Key, Count = kv.Value })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}