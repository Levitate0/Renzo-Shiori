using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models.Abstractions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Import;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models.Extensions;
using System.Collections.Concurrent;
using System.Globalization;

using RenzoBackend.Services.Scrobbling;

namespace RenzoBackend.Services.Search
{
    /// <summary>
    /// Service for search query operations following CQRS pattern
    /// </summary>
    public class SearchQueryService
    {
        // Interactive searches fan out across every installed provider and the
        // result is awaited by a live HTTP request, so a single slow provider on
        // the default 2-minute source timeout can push the whole search past an
        // upstream proxy's gateway timeout (a 524). Cap each provider tighter here
        // so the aggregate stays well under it; import/detail paths keep the long
        // default where latency is hidden behind a job.
        private static readonly TimeSpan InteractiveSearchTimeout = TimeSpan.FromSeconds(30);

        private readonly MihonBridgeService _mihon;
        private readonly SettingsService _settings;
        private readonly ProviderCacheService _providerCache;
        private readonly ThumbCacheService _thumb;
        private readonly IMemoryCache _memoryCache;
        private readonly AppDbContext _db;
        private readonly Providers.ProviderPreferencesService _providerPreferences;
        private readonly ILogger<SearchQueryService> _logger;


        public SearchQueryService(
            MihonBridgeService mihon,
            SettingsService settings,
            ProviderCacheService providerCache,
            IMemoryCache memoryCache,
            ThumbCacheService thumb,
            AppDbContext db,
            Providers.ProviderPreferencesService providerPreferences,
            ILogger<SearchQueryService> logger)
        {
            _mihon = mihon;
            _settings = settings;
            _providerCache = providerCache;
            _memoryCache = memoryCache;
            _thumb = thumb;
            _providerPreferences = providerPreferences;
            _db = db;
            _logger = logger;
        }

        /// <summary>
        /// Gets all available search sources based on preferred languages, filtered
        /// to the ones the requesting user has personally enabled. Installing a
        /// source doesn't make it visible to every user — each user opts in
        /// separately, regardless of who performed the underlying install.
        /// </summary>
        /// <param name="userId">The requesting user's id.</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>List of available search sources</returns>
        public async Task<List<SearchSourceDto>> GetAvailableSearchSourcesAsync(Guid userId, CancellationToken token = default)
        {
            var settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            var languages = settings.PreferredLanguages.ToList();
            if (languages.Count == 0)
            {
                languages = ["en"]; // Default to English if no languages set
            }

            var enabled = await GetEnabledProviderIdsAsync(userId, token).ConfigureAwait(false);
            var summarySources = await _providerCache.GetProviderSummariesForLanguagesAsync(languages, token).ConfigureAwait(false);
            List<SearchSourceDto> views = new List<SearchSourceDto>();
            foreach ((string mihonProviderId, SmallProviderDto summary) in summarySources)
            {
                if (!enabled.Contains(mihonProviderId))
                    continue;
                SearchSourceDto v = new SearchSourceDto
                {
                    Language = summary.Language,
                    Provider = summary.Provider,
                    Scanlator = summary.Scanlator,
                    IsStorage = summary.IsStorage,
                    ThumbnailUrl = summary.ThumbnailUrl,
                    Status = summary.Status,
                    Url = summary.Url,
                    MihonProviderId = mihonProviderId
                };
                views.Add(v);
            }
            return views;
        }

        /// <summary>MihonProviderIds the given user has personally enabled.</summary>
        private async Task<HashSet<string>> GetEnabledProviderIdsAsync(Guid userId, CancellationToken token)
        {
            List<string> ids = await _db.UserProviders
                .Where(p => p.UserId == userId)
                .Select(p => p.MihonProviderId)
                .ToListAsync(token).ConfigureAwait(false);
            return new HashSet<string>(ids, StringComparer.Ordinal);
        }

        /// <summary>
        /// Every installed, active source across every language — unfiltered by any
        /// user's personal enablement. Backs the "My sources" opt-in list, which is
        /// the only place a user can discover and enable a source they don't
        /// currently have visible.
        /// </summary>
        public async Task<List<SearchSourceDto>> GetAllInstalledSourcesAsync(CancellationToken token = default)
        {
            return await _db.Providers
                .Where(p => p.IsEnabled && !p.IsBroken && !p.IsDead)
                .Select(p => new SearchSourceDto
                {
                    MihonProviderId = p.MihonProviderId,
                    Provider = p.Provider,
                    Scanlator = p.Scanlator,
                    Language = p.Language,
                    IsStorage = p.IsStorage,
                })
                .ToListAsync(token).ConfigureAwait(false);
        }


        /// <summary>
        /// Searches for series across multiple sources with language and source filtering
        /// </summary>
        /// <param name="keyword">Search keyword</param>
        /// <param name="languages">List of language codes to search in</param>
        /// <param name="searchSources">Optional list of specific source IDs to search</param>
        /// <param name="threshold">Similarity threshold for linking series</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>List of linked series matching the search criteria</returns>
        public async Task<List<LinkedSeriesDto>> SearchSeriesAsync(string keyword, List<string> languages,
            List<string>? searchSources, double threshold, Guid userId, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(keyword) || languages == null || languages.Count == 0)
            {
                return [];
            }

            var appSettings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            var filteredSources = await _providerCache.GetSourcesForLanguagesAsync(languages, token).ConfigureAwait(false);

            // Per-user source visibility: search only ever runs against sources the
            // requesting user has personally enabled, regardless of what the client
            // passed in searchSources — installing a source doesn't make it usable
            // by everyone.
            var enabled = await GetEnabledProviderIdsAsync(userId, token).ConfigureAwait(false);
            filteredSources = filteredSources.Where(s => enabled.Contains(s.MihonProviderId)).ToList();

            string langs = languages.Count == 0 ? "all" : string.Join(",", languages);
            if (searchSources!=null && searchSources.Count>0)
                filteredSources = filteredSources.Where(s => searchSources.Contains(s.MihonProviderId)).ToList();
            _logger.LogInformation("Searching for '{keyword}' across {Count} providers in languages: {langs}", keyword, filteredSources.Count, langs);

            // Save-and-apply per-user source preferences (e.g. MangaDex content
            // rating): before searching, re-apply this user's own saved values for
            // any source where they've customized settings and the shared live
            // value has drifted since (another user saved theirs in the meantime).
            if (userId != Guid.Empty)
            {
                var packages = filteredSources.Select(s => s.SourcePackageName).Where(p => !string.IsNullOrEmpty(p)).Distinct().ToList();
                foreach (string pkg in packages)
                {
                    await _providerPreferences.SyncUserPreferencesIfNeededAsync(pkg!, userId, token).ConfigureAwait(false);
                }
            }

            return await SearchSeriesAsync(keyword, filteredSources, appSettings, threshold, token).ConfigureAwait(false);
        }

        public async Task<List<LinkedSeriesDto>> SearchSeriesAsync(List<(string keyword, ProviderStorageEntity ps)> sources, SettingsDto? appSettings, double threshold = 0.1f, CancellationToken token = default)
        {
            var results = new ConcurrentBag<(string Keyword, ProviderStorageEntity Storage, MangaList Result)>();
            var maxConcurrency = Math.Min(appSettings?.NumberOfSimultaneousSearches ?? 10, sources.Count);

            await Parallel.ForEachAsync(
                sources,
                new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = token },
                async (source, ct) =>
                {
                    // Try up to a few times for providers that are slow or have temporary issues.
                    // Each attempt is bounded by SourceTimeout so a single stuck provider can't hang
                    // the whole import; a timed-out or cancelled provider is not retried.
                    const int maxAttempts = 3;
                    for (int attempt = 1; attempt <= maxAttempts; attempt++)
                    {
                        try
                        {
                            var src = await _mihon.SourceFromProviderIdAsync(source.ps.MihonProviderId).ConfigureAwait(false);
                            var searchResult = await SourceTimeout
                                .RunAsync(c => src.SearchAsync(1, source.keyword, c), InteractiveSearchTimeout, ct)
                                .ConfigureAwait(false);
                            if (searchResult != null && searchResult.Mangas.Count > 0)
                            {
                                // Remove duplicates within the same source
                                var uniqueSeries = new List<ParsedManga>();
                                foreach (var series in searchResult.Mangas)
                                {
                                    if (uniqueSeries.All(a => a.Url != series.Url))
                                        uniqueSeries.Add(series);
                                }

                                searchResult.Mangas = uniqueSeries;
                                results.Add((source.keyword, source.ps, searchResult));
                            }
                            break; // success or empty result – done with this provider
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw; // the job itself was cancelled
                        }
                        catch (TimeoutException)
                        {
                            _logger.LogWarning("Search for provider {Name} timed out after {Seconds}s; skipping.", source.ps.Name, InteractiveSearchTimeout.TotalSeconds);
                            break; // a stuck provider won't recover by retrying
                        }
                        catch (HttpRequestException r)
                        {
                            _logger.LogWarning("Error searching provider {Name}: Http Error {StatusCode}.", source.ps.Name, r.StatusCode);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error searching provider {Name}: {Message}", source.ps.Name, ex.Message);
                            break; // unexpected error – don't spin
                        }
                    }

                }).ConfigureAwait(false);


            var allSeries = new List<(ParsedManga Manga, string MihonProiderId, string Language)>();
            foreach (var (pi, providerStorage, result) in results)
            {
                foreach (ParsedManga l in result.Mangas)
                {
                    if (!allSeries.Any(a=>a.Manga.Url == l.Url && a.MihonProiderId == providerStorage.MihonProviderId))
                        allSeries.Add((l, providerStorage.MihonProviderId, providerStorage.Language));
                }
            }

            var linked = allSeries.FindAndLinkSimilarSeries(threshold);


            Dictionary<string, ProviderStorageEntity> sourceDict = new Dictionary<string, ProviderStorageEntity>();
            foreach (var n in sources)
            {
                if (!sourceDict.ContainsKey(n.ps.MihonProviderId))
                    sourceDict.Add(n.ps.MihonProviderId, n.ps);
            }

            // Enrich linked series with provider information
            linked.ForEach(a =>
            {
                a.Provider = sourceDict[a.MihonProviderId!].Name;
                a.IsStorage = sourceDict[a.MihonProviderId!].IsStorage;
            });

            return linked.ToList();

        }


        /// <summary>
        /// Searches for series across specified sources with caching
        /// </summary>
        /// <param name="keyword">Search keyword</param>
        /// <param name="sources">Dictionary of sources to search</param>
        /// <param name="appSettings">Application settings</param>
        /// <param name="threshold">Similarity threshold for linking series</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>List of linked series matching the search criteria</returns>
        public async Task<List<LinkedSeriesDto>> SearchSeriesAsync(string keyword, List<ProviderStorageEntity> sources,
            SettingsDto? appSettings, double threshold = 0.1f, CancellationToken token = default)
        {
            try
            {
                Dictionary<string, ProviderStorageEntity> sourceDict = sources.ToDictionary(s => s.MihonProviderId, s => s);
                string cacheKey = "S" + keyword + threshold.ToString(CultureInfo.InvariantCulture) + "_" + string.Join(',', sourceDict.Keys);

                if (_memoryCache.TryGetValue(cacheKey, out List<LinkedSeriesDto>? cachedResult))
                {
                    _logger.LogInformation("Returning cached search result for keyword '{keyword}' with threshold {threshold}", keyword, threshold);
                    return cachedResult!;
                }
                // Execute parallel search across sources
                var results = new ConcurrentBag<(string providerId, string lang, MangaList)>();
                var maxConcurrency = Math.Min(appSettings?.NumberOfSimultaneousSearches ?? 10, sources.Count);

                await Parallel.ForEachAsync(
                    sourceDict.Keys,
                    new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = token },
                    async (providerId, ct) =>
                    {
                        try
                        {
                            var src = await _mihon.SourceFromProviderIdAsync(providerId).ConfigureAwait(false);
                            var searchResult = await SourceTimeout
                                .RunAsync(c => src.SearchAsync(1, keyword, c), InteractiveSearchTimeout, ct)
                                .ConfigureAwait(false);
                            if (searchResult != null && searchResult.Mangas.Count > 0)
                            {
                                // Remove duplicates within the same source
                                var uniqueSeries = new List<ParsedManga>();
                                foreach (var series in searchResult.Mangas)
                                {
                                    if (uniqueSeries.All(a => a.Url != series.Url))
                                        uniqueSeries.Add(series);
                                }

                                searchResult.Mangas = uniqueSeries;
                                results.Add((providerId, src.Language, searchResult));
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw; // the job itself was cancelled
                        }
                        catch (TimeoutException)
                        {
                            _logger.LogWarning("Search for provider {Name} timed out after {Seconds}s; skipping.", sourceDict[providerId].Name, InteractiveSearchTimeout.TotalSeconds);
                        }
                        catch (HttpRequestException r)
                        {
                            _logger.LogWarning("Error searching provider {Name}: Http Error {StatusCode}.", sourceDict[providerId].Name, r.StatusCode);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error searching provider {Name}: {Message}", sourceDict[providerId].Name, ex.Message);
                        }
                    }).ConfigureAwait(false);

                // Process and link similar series
                var allSeries = new List<(ParsedManga Manga, string ProviderId, string Language)>();
                foreach (var (providerId, lang, result) in results)
                {
                   
                    allSeries.AddRange(result.Mangas.Select(m => (m, providerId, lang)));
                }
                foreach(var n in allSeries)
                {
                    if (!string.IsNullOrEmpty(n.Manga.ThumbnailUrl))
                    {
                        await _thumb.AddUrlAsync(n.Manga.ThumbnailUrl, n.ProviderId, token).ConfigureAwait(false);
                    }
                }
                var linked = allSeries.FindAndLinkSimilarSeries(threshold);


                // Enrich linked series with provider information
                linked.ForEach(a =>
                {
                    a.Provider = sourceDict[a.MihonProviderId!].Name;
                    a.IsStorage = sourceDict[a.MihonProviderId!].IsStorage;
                });

                // Reorder results by fuzzy relevance to the search keyword
                var finalResults = linked.DistinctBy(a => a.MihonId).ToList();

                // Only try to reorder if we have a non-empty keyword
                if (!string.IsNullOrWhiteSpace(keyword) && finalResults.Count > 0)
                {
                    var candidates = finalResults
                        .Where(r => r.MihonId != null)
                        .Select(r => (r.Title, Id: r.MihonId!))
                        .ToList();

                    if (candidates.Count > 0)
                    {
                        var scored = TitleMatcher.MatchTitles(
                            originalTitles: new[] { keyword },
                            candidates: candidates,
                            minimumScore: 0);

                        var scoreLookup = scored.ToDictionary(s => s.Id, s => s.Percentage);

                        finalResults = finalResults
                            .OrderByDescending(r => r.MihonId != null && scoreLookup.TryGetValue(r.MihonId, out var score) ? score : -1)
                            .ThenBy(r => r.Title)
                            .ToList();
                    }
                }

                // Cache results for 30 seconds
                _memoryCache.Set(cacheKey, finalResults, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30) });

                return finalResults;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in SearchSeries");
                return [];
            }
        }
    }
}