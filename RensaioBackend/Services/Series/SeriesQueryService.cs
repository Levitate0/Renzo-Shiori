using RensaioBackend.Data;
using RensaioBackend.Extensions;
using RensaioBackend.Models.Database;
using RensaioBackend.Models.Dto;
using RensaioBackend.Services.Helpers;
using RensaioBackend.Services.Providers;
using RensaioBackend.Services.Settings;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Core.Extensions;
using System.Net;

namespace RensaioBackend.Services.Series
{
    /// <summary>
    /// Service responsible for querying series data
    /// </summary>
    public class SeriesQueryService
    {
        private readonly AppDbContext _db;
        private readonly SettingsService _settings;
        private readonly ProviderCacheService _providerCache;
        private readonly ILogger<SeriesQueryService> _logger;

        public SeriesQueryService(AppDbContext db, SettingsService settings, ProviderCacheService providerCache, ILogger<SeriesQueryService> logger)
        {
            _db = db;          
            _settings = settings;
            _providerCache = providerCache;
            _logger = logger;
        }

        // Upper bound on rows scanned when applying a genre filter client-side.
        // Genre is a value-converted CSV column EF can't translate a Contains/All
        // predicate over, so we stream FetchDate-desc rows and filter in memory up
        // to this cap to keep an unfiltered-heavy table from being fully walked.
        private const int MaxGenreScanRows = 20_000;

        /// <summary>
        /// Gets detailed information about a series by its unique identifier
        /// </summary>
        /// <param name="uid">The unique identifier of the series</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Extended information about the series</returns>
        public async Task<SeriesExtendedDto> GetSeriesAsync(Guid uid, CancellationToken token = default)
        {
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            Models.Database.SeriesEntity? s = await _db.Series
                .Include(a => a.Sources)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == uid, token);
            if (s == null)
                return new SeriesExtendedDto();
            return s.ToSeriesExtendedInfo(settings);
        }

        /// <summary>
        /// Gets the unified, series-level chapter list (merged across every source). For each
        /// chapter it reports whether a file is on disk and which source holds it, versus genuinely
        /// missing, plus the sources available for (re-)download. DB-only — no provider network call.
        /// </summary>
        /// <param name="seriesId">The unique identifier of the series.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task<List<ChapterDetailDto>> GetSeriesChaptersAsync(Guid seriesId, CancellationToken token = default)
        {
            Models.Database.SeriesEntity? s = await _db.Series
                .Include(a => a.Sources)
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == seriesId, token).ConfigureAwait(false);
            if (s == null)
                return new List<ChapterDetailDto>();
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
        public async Task<List<SeriesInfoDto>> GetLibraryAsync(CancellationToken token = default)
        {
            List<Models.Database.SeriesEntity> series = await _db.Series
                .Include(s => s.Sources).AsNoTracking().ToListAsync(token);
            return series.Select(a => a.ToSeriesInfo(_settings.DirectSettings)).ToList();
        }

        /// <summary>
        /// Builds the "Updates" feed (Suwayomi-style): one entry per chapter that
        /// finished downloading plus one entry per series added to the library,
        /// newest first. Derived entirely from existing data — chapter
        /// DownloadDate and series DateAdded (older rows without DateAdded fall
        /// back to their earliest chapter download).
        /// </summary>
        /// <param name="start">Starting index for pagination</param>
        /// <param name="count">Number of items to return</param>
        /// <param name="token">Cancellation token</param>
        public async Task<List<UpdateFeedItemDto>> GetUpdatesFeedAsync(int start, int count, CancellationToken token = default)
        {
            List<Models.Database.SeriesEntity> series = await _db.Series
                .Include(s => s.Sources).AsNoTracking().ToListAsync(token).ConfigureAwait(false);

            List<UpdateFeedItemDto> items = new();
            foreach (Models.Database.SeriesEntity s in series)
            {
                // One entry per chapter number; when several sources hold the same
                // chapter keep the most recent download of it.
                var chapterEvents = s.Sources
                    .SelectMany(p => p.Chapters, (p, c) => (Provider: p, Chapter: c))
                    .Where(x => x.Chapter.DownloadDate != null && !x.Chapter.IsDeleted && !string.IsNullOrEmpty(x.Chapter.Filename))
                    .GroupBy(x => x.Chapter.Number)
                    .Select(g => g.OrderByDescending(x => x.Chapter.DownloadDate).First())
                    .ToList();

                foreach ((SeriesProviderEntity p, Models.Chapter c) in chapterEvents)
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
                        Timestamp = c.DownloadDate!.Value
                    });
                }

                DateTime? added = s.DateAdded ?? chapterEvents.Min(x => x.Chapter.DownloadDate);
                if (added != null)
                {
                    items.Add(new UpdateFeedItemDto
                    {
                        SeriesId = s.Id,
                        SeriesTitle = s.Title,
                        ThumbnailUrl = s.ThumbnailUrl,
                        Kind = UpdateFeedItemDto.KindSeriesAdded,
                        Timestamp = added.Value
                    });
                }
            }

            return items
                .OrderByDescending(i => i.Timestamp)
                .Skip(start)
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
        /// <param name="token">Cancellation token</param>
        /// <returns>List of latest series information</returns>
        public async Task<List<LatestSeriesDto>> GetLatestAsync(int start, int count, string? mihonProviderId = null,
            string? keyword = null, IReadOnlyList<string>? genres = null, CancellationToken token = default)
        {
            IQueryable<LatestSerieEntity> series = _db.LatestSeries;
            if (!string.IsNullOrEmpty(mihonProviderId))
            {
                series = series.Where(a => a.MihonProviderId == mihonProviderId);
            }

            if (!string.IsNullOrEmpty(keyword))
                series = series.Where(a => EF.Functions.Like(a.Title, $"%{keyword}%"));

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
            await PopulateNsfwDetectionAsync(result, token).ConfigureAwait(false);
            return result;
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