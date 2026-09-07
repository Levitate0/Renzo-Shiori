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
        /// A run of this many chapters read consecutively from the same series
        /// collapses into one stacked entry. Matches the Updates feed's threshold,
        /// so a batch behaves the same way in both feeds.
        /// </summary>
        private const int HistoryStackThreshold = 5;

        /// <summary>
        /// Hard cap on the history feed, counted in ENTRIES — a stack is one.
        /// Applied after stacking on purpose: capping the raw chapter list first
        /// would let a single binge consume the whole feed and push every other
        /// series out, which is the opposite of what a history is for.
        /// </summary>
        private const int HistoryMaxEntries = 500;

        /// <summary>
        /// The user's reading history, newest first: every chapter they have opened,
        /// with runs from the same series collapsed into stacks.
        ///
        /// Built from read state rather than a dedicated log. Read state already
        /// records LastReadAt per chapter per user, so there is nothing to migrate
        /// and history works retroactively for everything already read. The cost is
        /// that it holds one entry per chapter, not one per re-read — reopening a
        /// chapter moves it up the feed instead of adding a second row.
        /// </summary>
        /// <param name="requesterId">The requesting user — only their own reads.</param>
        /// <param name="allowAll">True for an Owner-level requester viewing every library.</param>
        public async Task<List<HistoryFeedItemDto>> GetHistoryFeedAsync(int start, int count, Guid requesterId, bool allowAll, CancellationToken token = default)
        {
            string? username = await _db.Users
                .Where(u => u.Id == requesterId)
                .Select(u => u.Username)
                .FirstOrDefaultAsync(token).ConfigureAwait(false);
            if (string.IsNullOrEmpty(username))
                return [];

            IQueryable<Models.Database.SeriesEntity> seriesQuery = _db.Series.Include(s => s.Sources).AsNoTracking();
            if (!allowAll)
                seriesQuery = seriesQuery.Where(s => s.OwnerId == requesterId || s.OwnerId == Guid.Empty);
            List<Models.Database.SeriesEntity> series = await seriesQuery.ToListAsync(token).ConfigureAwait(false);

            // Flat list of "you read this chapter then" events.
            var events = new List<(Models.Database.SeriesEntity Series, HistoryChapterDto Chapter)>();
            foreach (Models.Database.SeriesEntity s in series)
            {
                if (string.IsNullOrWhiteSpace(s.StoragePath))
                    continue;

                List<Models.ReadState.ChapterReadState> states;
                try { states = _readState.GetSeriesReadStates(username, s.StoragePath); }
                catch (Exception ex)
                {
                    // One unreadable renzo.json must not take the whole feed down.
                    _logger.LogWarning(ex, "History: couldn't read state for '{Title}'; skipping it.", s.Title);
                    continue;
                }
                if (states.Count == 0)
                    continue;

                // Chapter names live on the series, not on read state. Built once
                // per series rather than per event.
                Dictionary<decimal, Models.Chapter> byNumber = s.Sources
                    .SelectMany(p => p.Chapters)
                    .Where(c => !c.IsDeleted && c.Number != null)
                    .GroupBy(c => c.Number!.Value)
                    .ToDictionary(g => g.Key, g => g.First());

                foreach (Models.ReadState.ChapterReadState st in states)
                {
                    // A state row exists as soon as a chapter is touched; only the
                    // ones with a real timestamp are history.
                    if (st.LastReadAt == default || st.LastReadAt.Year <= 1971)
                        continue;
                    byNumber.TryGetValue(st.ChapterNumber, out Models.Chapter? ch);
                    events.Add((s, new HistoryChapterDto
                    {
                        ChapterNumber = st.ChapterNumber,
                        ChapterName = ch?.Name,
                        Filename = st.LastReadFilename ?? ch?.Filename,
                        ReadAt = st.LastReadAt,
                        Progress = st.Progress,
                        Completed = st.IsCompleted,
                    }));
                }
            }

            events.Sort((a, b) => b.Chapter.ReadAt.CompareTo(a.Chapter.ReadAt));

            // Collapse consecutive same-series runs. "Consecutive" is in feed order,
            // so an unrelated series read in the middle of a binge splits it — the
            // stack describes what was actually read back-to-back.
            var entries = new List<HistoryFeedItemDto>();
            int i = 0;
            while (i < events.Count && entries.Count < start + count + HistoryMaxEntries)
            {
                Models.Database.SeriesEntity s = events[i].Series;
                int j = i + 1;
                while (j < events.Count && events[j].Series.Id == s.Id)
                    j++;

                int runLength = j - i;
                if (runLength >= HistoryStackThreshold)
                {
                    List<HistoryChapterDto> chapters = events.GetRange(i, runLength)
                        .Select(e => e.Chapter)
                        .OrderByDescending(c => c.ChapterNumber ?? decimal.MinValue)
                        .ToList();
                    entries.Add(new HistoryFeedItemDto
                    {
                        SeriesId = s.Id,
                        SeriesTitle = s.Title,
                        ThumbnailUrl = s.ThumbnailUrl,
                        Kind = HistoryFeedItemDto.KindStack,
                        ReadAt = events[i].Chapter.ReadAt,
                        Chapters = chapters,
                    });
                }
                else
                {
                    for (int k = i; k < j; k++)
                    {
                        HistoryChapterDto c = events[k].Chapter;
                        entries.Add(new HistoryFeedItemDto
                        {
                            SeriesId = s.Id,
                            SeriesTitle = s.Title,
                            ThumbnailUrl = s.ThumbnailUrl,
                            Kind = HistoryFeedItemDto.KindChapter,
                            ReadAt = c.ReadAt,
                            ChapterNumber = c.ChapterNumber,
                            ChapterName = c.ChapterName,
                            Filename = c.Filename,
                            Progress = c.Progress,
                            Completed = c.Completed,
                        });
                    }
                }
                i = j;
            }

            return entries
                .Take(HistoryMaxEntries)
                .Skip(Math.Max(0, start))
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Gets the latest series with optional filtering
        /// </summary>
        /// <param name="start">Starting index for pagination</param>
        /// <param name="count">Number of items to return</param>
        /// <param name="sourceid">Optional source ID filter</param>
        /// <param name="keyword">Optional keyword filter</param>
        /// <param name="genres">Optional tag/genre filter; a row must carry every supplied tag (AND semantics)</param>
        /// <param name="excludeGenres">Optional negative tag filter; a row carrying ANY of these is dropped. Applied
        /// independently of <paramref name="genres"/>, so excluding alone is a valid filter.</param>
        /// <param name="requesterId">The requesting user's id — the "in library" badge reflects only their own series.</param>
        /// <param name="allowAll">True for an Owner-level requester viewing every library (in-library badge then matches any owner).</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>List of latest series information</returns>
        public async Task<List<LatestSeriesDto>> GetLatestAsync(int start, int count, string? mihonProviderId,
            string? keyword, IReadOnlyList<string>? genres, IReadOnlyList<string>? excludeGenres,
            Guid requesterId, bool allowAll, CancellationToken token = default)
        {
            // Keyword searches go to the full-catalog path: the cached feed only
            // contains series that appeared in a source's latest/popular listing,
            // so a plain LIKE over it misses everything older — instead we merge
            // the cached rows with a live search across the sources themselves.
            if (!string.IsNullOrWhiteSpace(keyword))
                return await GetKeywordCatalogPageAsync(start, count, mihonProviderId, keyword, genres, excludeGenres, requesterId, allowAll, token).ConfigureAwait(false);

            // Single-source browse (no keyword): the cache for a given source only
            // holds whatever its last scheduled latest/popular job pulled — often a
            // page or two — so a freshly added or thin source shows almost nothing.
            // Merge the cache with a live multi-page catalog fetch of that source.
            if (!string.IsNullOrEmpty(mihonProviderId))
                return await GetSourceCatalogPageAsync(start, count, mihonProviderId, genres, excludeGenres, requesterId, allowAll, token).ConfigureAwait(false);

            // All-sources, no keyword: serve the aggregate cache immediately, and
            // (on the first page only) kick off a background sweep that pulls every
            // enabled source's live catalog into the cache so the feed keeps filling
            // out. Non-blocking + rate-limited so it never hammers sources.
            if (start == 0)
                KickOffAllSourcesCatalogFill();

            HashSet<string>? enabledForBrowse = await GetEnabledProviderIdsOrNullAsync(requesterId, allowAll, token).ConfigureAwait(false);
            if (enabledForBrowse != null && enabledForBrowse.Count == 0)
                return []; // nothing enabled yet — an empty Browse, not "everything"

            IQueryable<LatestSerieEntity> series;

            // Normalize both halves of the tag filter; null/empty (after trimming
            // blanks) means "no filter" for that half. They are independent — an
            // exclude-only filter is valid and common ("everything except Ecchi").
            List<string>? normalizedGenres = NormalizeGenres(genres);
            List<string>? normalizedExcludes = NormalizeGenres(excludeGenres);

            // Genre is a value-converted CSV column, so EF can't translate a
            // predicate over it. This used to stream FetchDate-desc rows and match
            // in memory up to a 20k scan cap — which quietly made tag filtering a
            // lie: the tag picker counts across the WHOLE catalogue (~475k rows),
            // so it would advertise "Psychological (25,914)" while the filter could
            // only ever see the newest 20k rows and reach ~900 of them. Measured
            // reachability was 3-8% depending on the tag, and worse for AND-combos.
            //
            // Pushing the match into SQL fixes both halves: it sees every row, and
            // it's FASTER, because IX_LatestSerie_FetchDate lets SQLite walk in
            // FetchDate order and stop as soon as the page is full instead of
            // materialising 20k entities (measured 2-15ms/page vs a 20k-row scan).
            if (normalizedGenres != null || normalizedExcludes != null)
                series = ApplyGenreFilterSql(normalizedGenres, normalizedExcludes);
            else
                series = _db.LatestSeries;

            if (!string.IsNullOrEmpty(mihonProviderId))
                series = series.Where(a => a.MihonProviderId == mihonProviderId);
            if (enabledForBrowse != null)
                series = series.Where(a => a.MihonProviderId != null && enabledForBrowse.Contains(a.MihonProviderId));

            series = series.OrderByDescending(a => a.FetchDate);
            if (start > 0)
                series = series.Skip(start);

            List<LatestSeriesDto> result = (await series.Take(count).ToListAsync(token).ConfigureAwait(false))
                .Select(a => a.ToSeriesInfo()).ToList();
            await PopulateOwnerLibraryStatusAsync(result, requesterId, allowAll, token).ConfigureAwait(false);
            await PopulateNsfwDetectionAsync(result, token).ConfigureAwait(false);
            return result;
        }

        /// <summary>
        /// Trims/dedupes an incoming tag filter. Returns null for "no filter" so
        /// callers can take the plain pagination path.
        /// </summary>
        private static List<string>? NormalizeGenres(IReadOnlyList<string>? genres)
        {
            if (genres == null || genres.Count == 0)
                return null;
            List<string> normalized = genres
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return normalized.Count == 0 ? null : normalized;
        }

        /// <summary>
        /// A LatestSeries query filtered to rows carrying EVERY tag in
        /// <paramref name="include"/> (AND semantics) and NONE of the tags in
        /// <paramref name="exclude"/>, matched in SQL. Either half may be null.
        ///
        /// Genre is persisted as a CSV string by a value converter, so the match is
        /// done by wrapping both sides in commas — <c>',Action,Romance,' LIKE '%,Action,%'</c>
        /// — which prevents a tag matching a substring of a different one ("Art"
        /// inside "Martial Arts"). <c>replace(Genre, ', ', ',')</c> normalizes the
        /// spacing some sources emit. SQLite's LIKE is case-insensitive for ASCII,
        /// which matches the OrdinalIgnoreCase semantics used elsewhere.
        ///
        /// Tags are passed as PARAMETERS, never interpolated; LIKE wildcards inside
        /// a tag are escaped so a tag containing % or _ can't widen the match.
        ///
        /// Two asymmetries between the halves, both load-bearing:
        ///
        /// 1. The <c>Genre IS NOT NULL AND Genre &lt;&gt; ''</c> guard applies only when
        ///    there is something to INCLUDE. An untagged row cannot carry a wanted
        ///    tag, but it equally cannot carry an unwanted one — so on an
        ///    exclude-only filter it must survive. Keeping the guard unconditional
        ///    would silently hide every untagged series behind "not Ecchi".
        /// 2. Exclusions COALESCE Genre to '' before matching. In SQL
        ///    <c>NULL NOT LIKE x</c> is NULL, not true, so a NULL Genre would fail
        ///    the WHERE and drop the row — the same bug as (1), arriving by a
        ///    different route and surviving (1)'s fix.
        /// </summary>
        private IQueryable<LatestSerieEntity> ApplyGenreFilterSql(List<string>? include, List<string>? exclude)
        {
            int includeCount = include?.Count ?? 0;
            int excludeCount = exclude?.Count ?? 0;
            var clauses = new List<string>(includeCount + excludeCount);
            var args = new List<object>(includeCount + excludeCount);

            for (int i = 0; i < includeCount; i++)
            {
                clauses.Add($"(',' || replace(\"Genre\", ', ', ',') || ',') LIKE ('%,' || {{{args.Count}}} || ',%') ESCAPE '\\'");
                args.Add(EscapeLike(include![i]));
            }
            for (int i = 0; i < excludeCount; i++)
            {
                clauses.Add($"(',' || replace(COALESCE(\"Genre\", ''), ', ', ',') || ',') NOT LIKE ('%,' || {{{args.Count}}} || ',%') ESCAPE '\\'");
                args.Add(EscapeLike(exclude![i]));
            }

            string where = string.Join(" AND ", clauses);
            if (includeCount > 0)
                where = "\"Genre\" IS NOT NULL AND \"Genre\" <> '' AND " + where;

            return _db.LatestSeries.FromSqlRaw("SELECT * FROM \"LatestSeries\" WHERE " + where, args.ToArray());
        }

        /// <summary>
        /// The in-memory twin of <see cref="ApplyGenreFilterSql"/>, for rows that
        /// never went through SQL — live source results merged into a page. Kept
        /// beside it deliberately: the two must agree, and a row that survives one
        /// but not the other shows up as a result that flickers in and out as the
        /// live fetch lands.
        /// </summary>
        private static bool MatchesTagFilter(IReadOnlyList<string>? rowGenres, List<string>? include, List<string>? exclude)
        {
            bool untagged = rowGenres == null || rowGenres.Count == 0;
            // Untagged rows can satisfy an exclusion but never an inclusion.
            if (untagged)
                return include is not { Count: > 0 };

            var rowSet = new HashSet<string>(rowGenres!.Select(g => g.Trim()), StringComparer.OrdinalIgnoreCase);
            if (include is { Count: > 0 } && !include.All(rowSet.Contains))
                return false;
            if (exclude is { Count: > 0 } && exclude.Any(rowSet.Contains))
                return false;
            return true;
        }

        /// <summary>Escapes LIKE wildcards so a tag matches literally.</summary>
        private static string EscapeLike(string value) =>
            value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

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

        // How long a browse request waits for the live source search when it does
        // not already have enough stored rows to fill the page being asked for.
        //
        // Waiting at all is the point: sources are searched 10 at a time
        // (NumberOfSimultaneousSearches) with a 30s cap each, so a 50-source sweep
        // runs in waves, and the old 15s budget expired mid-sweep almost every
        // time. The request then returned the handful of stored rows and the rest
        // surfaced on some later request — which reads as results appearing and
        // disappearing at random rather than as a search still running.
        //
        // It is NOT a "wait for every source" timer. A request that can already
        // fill its page skips the wait entirely (see GetKeywordCatalogPageAsync),
        // so this only ever delays a screen that would otherwise render nearly
        // empty. Scrolling still reveals whatever landed since.
        //
        // Kept under the ~100s at which reverse proxies (the Cloudflare tunnel
        // included) cut off an idle request, so an overrun still returns rows
        // instead of failing outright.
        private const int LiveSearchBudgetMs = 60_000;

        /// <summary>
        /// Full-catalog browse search: merges the cached latest/popular feed with
        /// a live search across the actual sources, so titles that never appeared
        /// in a latest listing (old completed series, brand-new sources) are found.
        /// </summary>
        private async Task<List<LatestSeriesDto>> GetKeywordCatalogPageAsync(int start, int count,
            string? mihonProviderId, string keyword, IReadOnlyList<string>? genres, IReadOnlyList<string>? excludeGenres,
            Guid requesterId, bool allowAll, CancellationToken token)
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

            // Wait for the live sweep only when the stored rows can't fill the page
            // being asked for. A screen's worth already in hand renders now and the
            // sweep lands in cache for the next scroll; a nearly-empty screen is
            // worth waiting on, because showing two of seven sources and filling the
            // rest in later is what looked broken.
            bool canFillPage = merged.Count >= Math.Max(0, start) + count;
            List<LatestSeriesDto> live = await GetLiveSearchRowsAsync(
                keyword, mihonProviderId, canFillPage ? 0 : LiveSearchBudgetMs, token).ConfigureAwait(false);
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

            // Tag filter, same semantics as the non-keyword path: carry every
            // wanted tag, carry none of the unwanted ones. This whole path is
            // in-memory (the merge of cached rows and a live source search), so
            // there is no SQL half to keep in step here.
            List<string>? wantedGenres = NormalizeGenres(genres);
            List<string>? unwantedGenres = NormalizeGenres(excludeGenres);
            if (wantedGenres != null || unwantedGenres != null)
                merged = merged.Where(row => MatchesTagFilter(row.Genre, wantedGenres, unwantedGenres)).ToList();

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
        /// <param name="budgetMs">How long to wait for the fan-out. Zero starts it and
        /// returns immediately — the caller already has enough to render, and the
        /// result still lands in the cache for the next request.</param>
        private async Task<List<LatestSeriesDto>> GetLiveSearchRowsAsync(string keyword, string? mihonProviderId, int budgetMs, CancellationToken token)
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

            if (budgetMs <= 0)
            {
                if (!searchTask.IsCompleted)
                    return [];   // enough stored rows to render; don't stall on the sweep
            }
            else
            {
                Task finished = await Task.WhenAny(searchTask, Task.Delay(budgetMs, token)).ConfigureAwait(false);
                if (finished != searchTask)
                {
                    _logger.LogInformation("Live browse search for '{Keyword}' still running after {Budget}ms; returning cached rows for now.", keyword, budgetMs);
                    return [];
                }
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
            string mihonProviderId, IReadOnlyList<string>? genres, IReadOnlyList<string>? excludeGenres,
            Guid requesterId, bool allowAll, CancellationToken token)
        {
            // Cached rows for this source. Without a tag filter we take the newest
            // LiveCatalogMaxRows and merge the live catalogue on top. WITH one, that
            // cap would be applied BEFORE the filter — matching tags against only
            // the newest 400 rows of a source that may hold 78k — so the filter runs
            // in SQL across all of the source's rows instead (same reasoning as the
            // all-sources path above).
            List<string>? sourceGenres = NormalizeGenres(genres);
            List<string>? sourceExcludes = NormalizeGenres(excludeGenres);
            IQueryable<LatestSerieEntity> cached = sourceGenres != null || sourceExcludes != null
                ? ApplyGenreFilterSql(sourceGenres, sourceExcludes)
                : _db.LatestSeries;

            List<LatestSeriesDto> merged = (await cached
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

            // Cached rows came back already filtered; this still has to police the
            // LIVE rows merged in above, which never went through SQL.
            if (sourceGenres != null || sourceExcludes != null)
                merged = merged.Where(row => MatchesTagFilter(row.Genre, sourceGenres, sourceExcludes)).ToList();

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
            var catalogBridgeInfo = new Dictionary<string, string?>(StringComparer.Ordinal);

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
                    // The parsed manga is the payload ToManga() needs to open this row
                    // in the reader. It was dropped on the floor here, so catalog rows
                    // were browsable but not readable — the same defect the search path
                    // had, arrived at from the other direction.
                    catalogBridgeInfo[mihonId] = JsonSerializer.Serialize<Manga>(m);
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
                            BridgeItemInfo = catalogBridgeInfo.GetValueOrDefault(r.MihonId),
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
            // Kept alongside the rows so they can be persisted below. ToManga()
            // reads this and nothing else, so a row stored without it is visible in
            // Browse but cannot be opened.
            var bridgeInfo = new Dictionary<string, string?>(StringComparer.Ordinal);
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
                bridgeInfo[l.MihonId] = l.BridgeItemInfo;
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

            // Persist what the search found, exactly as the live CATALOG fetch below
            // already does for its rows.
            //
            // Without this, a search-only row existed nowhere but this method's
            // return value. Two things followed, and both were reported as bugs:
            // the card vanished from Browse the moment the in-memory result went
            // (leaving only the handful of rows the update jobs had stored), and
            // opening one 404'd instantly — the reader resolves a browse row by
            // looking its MihonId up in LatestSeries, so a row that was never
            // stored cannot be read, whatever the source would have served.
            //
            // Insert-only: an existing row may carry richer job-populated data.
            if (rows.Count > 0)
            {
                try
                {
                    var db = scope.ServiceProvider.GetRequiredService<Data.AppDbContext>();
                    var ids = rows.Select(r => r.MihonId).ToList();
                    var existing = await db.LatestSeries
                        .Where(a => ids.Contains(a.MihonId))
                        .Select(a => a.MihonId)
                        .ToListAsync().ConfigureAwait(false);
                    var have = new HashSet<string>(existing, StringComparer.Ordinal);

                    // Seeded a year back, decremented by result position — the same
                    // convention the catalog fetch uses. A search hit carries no real
                    // update time, and stamping it "now" would park arbitrary search
                    // results at the top of the recently-updated feed.
                    DateTime seededBase = DateTime.UtcNow.AddYears(-1);
                    int order = 0;
                    foreach (LatestSeriesDto r in rows)
                    {
                        if (!have.Add(r.MihonId))
                            continue;
                        db.LatestSeries.Add(new LatestSerieEntity
                        {
                            MihonId = r.MihonId,
                            MihonProviderId = r.MihonProviderId,
                            BridgeItemInfo = bridgeInfo.GetValueOrDefault(r.MihonId),
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
                            FetchDate = seededBase.AddSeconds(-order++),
                        });
                    }
                    await db.SaveChangesAsync().ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogWarning(e, "Failed to persist live search rows for '{Keyword}'.", keyword);
                }
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

            // Query by the raw title AND by a decoration-stripped variant. Sources
            // publish the same work under decorated names — "Never Just Friends Raw"
            // on one, "Never Just Friends" on another — and the adult tags often sit
            // only on the undecorated row. An exact-title join therefore missed
            // precisely the rows that need this fallback most: an untagged listing
            // on a dedicated adult source. Matching is then done on a normalized key
            // so punctuation and spacing differences don't split the group either.
            List<string> titles = pending
                .SelectMany(r => TitleQueryKeys(r.Title))
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
                    adultTitles.Add(NormalizeTitleKey(m.Title));
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
                adultTitles.Add(NormalizeTitleKey(s.Title));
            }

            foreach (LatestSeriesDto row in pending)
            {
                if ((row.SeriesId != null && adultSeriesIds.Contains(row.SeriesId.Value)) ||
                    adultTitles.Contains(NormalizeTitleKey(row.Title)))
                {
                    row.IsNsfw = true;
                }
            }
        }

        // Decorations sources bolt onto a title that don't change which work it is.
        private static readonly string[] TitleDecorations =
        {
            "raw", "uncensored", "uncut", "official", "colored", "full color",
            "fan colored", "manhwa", "manhua", "webtoon",
        };

        /// <summary>
        /// Lowercased titles to look the row up by: the title as given, plus the
        /// same title with trailing decorations peeled off.
        /// </summary>
        private static IEnumerable<string> TitleQueryKeys(string? title)
        {
            string raw = (title ?? string.Empty).Trim().ToLowerInvariant();
            if (raw.Length == 0)
                yield break;
            yield return raw;
            string stripped = StripTitleDecorations(raw);
            if (stripped.Length > 0 && stripped != raw)
                yield return stripped;
        }

        private static string StripTitleDecorations(string lowered)
        {
            string current = lowered;
            bool changed = true;
            while (changed)
            {
                changed = false;
                current = current.Trim().Trim('-', '–', ':', '|', '(', ')', '[', ']').Trim();
                foreach (string d in TitleDecorations)
                {
                    if (current.Length > d.Length + 1 && current.EndsWith(d, StringComparison.Ordinal))
                    {
                        char before = current[current.Length - d.Length - 1];
                        if (before is ' ' or '-' or '(' or '[' or ':' or '|')
                        {
                            current = current[..(current.Length - d.Length)];
                            changed = true;
                        }
                    }
                }
            }
            return current.Trim().Trim('-', '–', ':', '|', '(', ')', '[', ']').Trim();
        }

        /// <summary>
        /// Comparison key for grouping the same work across sources: decorations
        /// removed, then everything but letters and digits dropped so spacing and
        /// punctuation differences don't split it.
        /// </summary>
        private static string NormalizeTitleKey(string? title)
        {
            string stripped = StripTitleDecorations((title ?? string.Empty).Trim().ToLowerInvariant());
            var sb = new System.Text.StringBuilder(stripped.Length);
            foreach (char ch in stripped)
            {
                if (char.IsLetterOrDigit(ch))
                    sb.Append(ch);
            }
            return sb.ToString();
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