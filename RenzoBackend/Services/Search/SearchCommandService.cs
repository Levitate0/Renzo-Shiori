using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Import;
using RenzoBackend.Services.Series;
using RenzoBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Models.Extensions;
using System.Collections.Concurrent;
using ExtensionChapter = Mihon.ExtensionsBridge.Models.Extensions.Chapter;
using ExtensionManga = Mihon.ExtensionsBridge.Models.Extensions.Manga;

namespace RenzoBackend.Services.Search
{
    /// <summary>
    /// Service for search command operations following CQRS pattern
    /// </summary>
    public class SearchCommandService
    {

        private readonly SettingsService _settings;

        private readonly AppDbContext _db;
        private readonly ILogger<SearchCommandService> _logger;
        private readonly MihonBridgeService _mihon;
        private readonly Series.SeriesCategoryResolver _categoryResolver;

        public SearchCommandService(
            SettingsService settings,
            AppDbContext db,
            MihonBridgeService mihon,
            Series.SeriesCategoryResolver categoryResolver,
            ILogger<SearchCommandService> logger)
        {
            _settings = settings;
            _db = db;
            _logger = logger;
            _mihon = mihon;
            _categoryResolver = categoryResolver;
        }

        /// <summary>
        /// Augments a list of LinkedSeries with full details by fetching complete information from Suwayomi
        /// </summary>
        /// <param name="linkedSeries">List of linked series to augment</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Augmented response with complete series information</returns>
        public async Task<AugmentedResponseDto> AugmentSeriesAsync(List<LinkedSeriesDto> linkedSeries, CancellationToken token = default)
        {
            if (linkedSeries == null || linkedSeries.Count == 0)
            {
                return new AugmentedResponseDto();
            }
            try
            {
                var appSettings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
                var providerTitles = linkedSeries.Select(a => a.Title).ToList();

                // Get existing series providers to check for continuation logic
                var existingSeries = await _db.SeriesProviders
                    .Where(sp => providerTitles.Contains(sp.Title))
                    .AsNoTracking()
                    .ToListAsync(token).ConfigureAwait(false);
                
                existingSeries = existingSeries.Where(a => linkedSeries.Any(ls => ls.Lang == a.Language && ls.Title == a.Title)).ToList();

                // Fetch full series data in parallel
                var seriesDetailsMap = new ConcurrentDictionary<string, (ParsedManga, List<ParsedChapter>)>();
                // Sources we couldn't use, with why — surfaced to the wizard so a dead-end
                // "Next" explains itself (no chapters in your languages vs. source unreachable).
                var droppedSeries = new ConcurrentBag<DroppedSeriesDto>();
                var validSeries = linkedSeries.Where(ls => !string.IsNullOrEmpty(ls.MihonId)).ToList();
                var maxConcurrency = Math.Min(appSettings.NumberOfSimultaneousSearches, validSeries.Count);
                var parallelOptions = new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxConcurrency,
                    CancellationToken = token
                };
                  
                await Parallel.ForEachAsync(validSeries, parallelOptions, async (ls, ct) =>
                {
                    try
                    {
                        var source = await _mihon.SourceFromProviderIdAsync(ls.MihonProviderId!, token).ConfigureAwait(false);
                        Manga m = ls.ToManga()!;
                        ParsedManga? fullData = null;
                        List<ParsedChapter>? chapterData = null;
                        // Retry a few times with backoff. Sources like MangaDex 429 under
                        // the app's concurrent background load, and a transient empty/failed
                        // chapter fetch would otherwise silently drop the series and dead-end
                        // "Add Series". Bound each source call so a stuck provider can't freeze.
                        for (int attempt = 0; attempt < 3; attempt++)
                        {
                            if (attempt > 0)
                                await Task.Delay(TimeSpan.FromMilliseconds(800 * attempt), ct).ConfigureAwait(false);
                            fullData = await SourceTimeout
                                .RunAsync(c => source.GetDetailsAsync(m, c), ct)
                                .ConfigureAwait(false);
                            chapterData = await SourceTimeout
                                .RunAsync(c => source.GetChaptersAsync(m, c), ct)
                                .ConfigureAwait(false);
                            if (fullData != null && chapterData != null && chapterData.Count > 0)
                                break;
                        }
                        if (fullData != null && chapterData != null && chapterData.Count > 0)
                        {
                            // Set default scanlator if not provided
                            chapterData.ForEach(a =>
                            {
                                if (string.IsNullOrEmpty(a.Scanlator))
                                    a.Scanlator = ls.Provider;
                            });
                            seriesDetailsMap.TryAdd(ls.MihonId!, (fullData, chapterData));
                        }
                        else
                        {
                            // Empty result WITHOUT an exception. If details loaded but there
                            // were 0 chapters, the source has nothing in the enabled
                            // languages (e.g. a title only translated to a language the user
                            // hasn't enabled), or its content-rating filter hid them. If even
                            // details were missing, the source is effectively unreachable.
                            bool detailsOk = fullData != null;
                            droppedSeries.Add(new DroppedSeriesDto
                            {
                                Title = ls.Title,
                                Provider = ls.Provider,
                                Reason = detailsOk ? "no-chapters" : "unreachable",
                            });
                            _logger.LogWarning(
                                "Augment: '{Title}' from {Provider} came back with {DetailsState} and {ChapterCount} chapters after retries — dropping. Likely no chapters in the enabled languages, a content-rating filter, or rate-limiting.",
                                ls.Title, ls.Provider, detailsOk ? "details" : "no details", chapterData?.Count ?? 0);
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw; // the job itself was cancelled
                    }
                    catch (TimeoutException)
                    {
                        droppedSeries.Add(new DroppedSeriesDto { Title = ls.Title, Provider = ls.Provider, Reason = "unreachable" });
                        _logger.LogWarning("Fetching details for {Title} from {Provider} timed out after {Seconds}s; skipping.", ls.Title, ls.Provider, SourceTimeout.DefaultTimeout.TotalSeconds);
                    }
                    catch (HttpRequestException r)
                    {
                        droppedSeries.Add(new DroppedSeriesDto { Title = ls.Title, Provider = ls.Provider, Reason = "unreachable" });
                        _logger.LogWarning("Error fetching series details for {Title} from {Provider}: Http Error {StatusCode}.", ls.Title, ls.Provider, r.StatusCode);
                    }
                    catch (Exception ex)
                    {
                        droppedSeries.Add(new DroppedSeriesDto { Title = ls.Title, Provider = ls.Provider, Reason = "unreachable" });
                        _logger.LogError(ex, "Error fetching details for series ID {Title}: {Message}", ls.Title, ex.Message);
                    }
                }).ConfigureAwait(false);

                // Convert to ProviderSeriesDetails objects
                var ProviderSeriesDetailsResults = new List<ProviderSeriesDetails>();
                var categories = appSettings.Categories ?? [];

                foreach (var ls in linkedSeries)
                {
                    if (string.IsNullOrEmpty(ls.MihonId) || !seriesDetailsMap.TryGetValue(ls.MihonId, out var details))
                    {
                        continue;
                    }

                    details.Item2.FillMissingChapterNumbers();

                    var ProviderSeriesDetails = new ProviderSeriesDetails
                    {
                        MihonId = ls.MihonId,
                        MihonProviderId = ls.MihonProviderId,
                        BridgeItemInfo = ls.BridgeItemInfo,
                        Provider = ls.Provider,
                        Scanlator = ls.Provider,
                        Lang = ls.Lang,
                        Title = details.Item1.Title,
                        ThumbnailUrl = details.Item1.ThumbnailUrl,
                        Artist = details.Item1.Artist ?? string.Empty,
                        Author = details.Item1.Author ?? string.Empty,
                        Description = details.Item1.Description ?? string.Empty,
                        Genre = details.Item1.GetGenres(),
                        ChapterCount = details.Item2?.Count ?? 0,
                        Url = details.Item1.RealUrl,
                        SuggestedFilename = details.Item1.Title.MakeFolderNameSafe(),
                        Status = (SeriesStatus)(int)details.Item1.Status,
                        IsStorage = ls.IsStorage,
                    };

                    ProviderSeriesDetails.Type = ProviderSeriesDetails.Genre.DeriveTypeFromGenre(categories);

                    // Group chapters by scanlator
                    var groupedChapters = details.Item2?
                        .GroupBy(c => c.Scanlator)
                        .ToDictionary(g => g.Key ?? "", g => g.ToList());

                    var seriesPerScanlator = new List<ProviderSeriesDetails>();
                    foreach (var scanlatorGroup in groupedChapters)
                    {
                        var seriesCopy = FastDeepCloner.DeepCloner.Clone(ProviderSeriesDetails);
                        var firstChapter = scanlatorGroup.Value.First();
                        
                        seriesCopy.Scanlator = scanlatorGroup.Key;
                        seriesCopy.LastUpdatedUTC = firstChapter.DateUpload.DateTime;
                        seriesCopy.ChapterCount = scanlatorGroup.Value.Count;
                        seriesCopy.Chapters = scanlatorGroup.Value.Select(a => a.ToChapter()).OrderBy(a => a.ProviderIndex).ToList();
                        seriesCopy.ChapterList = scanlatorGroup.Value.Select(a => a.ParsedNumber).FormatDecimalRanges();
                        
                        seriesPerScanlator.Add(seriesCopy);
                    }

                    // Apply existing provider logic
                    var existingForProvider = existingSeries.Where(a => a.MihonProviderId == ls.MihonProviderId && a.Language == ls.Lang && ls.Title == a.Title).ToList();
                    foreach (var ProviderSeriesDetailsItem in seriesPerScanlator)
                    {
                        var existingProvider = existingForProvider.FirstOrDefault(a => a.MihonProviderId == ProviderSeriesDetailsItem.MihonProviderId && 
                            a.Title == ProviderSeriesDetailsItem.Title && 
                            a.Language == ProviderSeriesDetailsItem.Lang && 
                            a.Scanlator == ProviderSeriesDetailsItem.Scanlator);
                        
                        if (existingProvider != null)
                        {
                            ProviderSeriesDetailsItem.ExistingProvider = true;
                            if (existingProvider.Status == SeriesStatus.ONGOING && existingProvider.Chapters.Count > 0)
                                ProviderSeriesDetailsItem.ContinueAfterChapter = (int)(existingProvider.Chapters.Max(a => a.Number) ?? 0m);
                            else
                                ProviderSeriesDetailsItem.ContinueAfterChapter = null;
                        }
                    }

                    ProviderSeriesDetailsResults.AddRange(seriesPerScanlator);
                }

                // Apply type derivation logic
                if (ProviderSeriesDetailsResults.All(a => a.Type == null))
                {
                    ProviderSeriesDetailsResults.ForEach(a => { a.Type = a.Genre.DeriveTypeFromGenre(categories, true); });
                }

                var inferredType = ProviderSeriesDetailsResults.FirstOrDefault(a => a.Type != null)?.Type;
                if (inferredType != null)
                {
                    ProviderSeriesDetailsResults.Where(a => a.Type == null).ToList().ForEach(a => a.Type = inferredType);
                }

                // Resolve the real category now (after sources are chosen, before the
                // confirm step) via MangaDex country-of-origin, so a new series lands
                // in the right folder from the start instead of the genre guess. Bounded
                // so it can't hang the wizard — if MangaDex is slow it falls through to
                // the genre type and the post-add background pass corrects it later.
                if (appSettings.CategorizedFolders && categories.Length > 0 && ProviderSeriesDetailsResults.Count > 0)
                {
                    try
                    {
                        var primary = ProviderSeriesDetailsResults.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.Title))
                                      ?? ProviderSeriesDetailsResults[0];
                        IEnumerable<string> allGenres = ProviderSeriesDetailsResults.SelectMany(a => a.Genre ?? new List<string>());
                        IEnumerable<string> provNames = ProviderSeriesDetailsResults.Select(a => a.Provider).Where(p => !string.IsNullOrWhiteSpace(p))!;

                        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        cts.CancelAfter(TimeSpan.FromSeconds(10));
                        var res = await _categoryResolver
                            .ResolveAsync(primary.Title, allGenres, provNames, categories, cts.Token)
                            .ConfigureAwait(false);
                        if (res.Confident && !string.IsNullOrEmpty(res.Category))
                        {
                            ProviderSeriesDetailsResults.ForEach(a => a.Type = res.Category);
                            _logger.LogInformation("Add wizard: categorized '{Title}' as {Cat} ({Signal})", primary.Title, res.Category, res.Signal);
                        }
                    }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested)
                    {
                        _logger.LogDebug("Add wizard category lookup timed out; using genre type, background pass will correct.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Add wizard category lookup failed; using genre type.");
                    }
                }

                return new AugmentedResponseDto
                {
                    Series = ProviderSeriesDetailsResults,
                    StorageFolderPath = appSettings.StorageFolder,
                    UseCategoriesForPath = appSettings.CategorizedFolders,
                    Categories = appSettings.Categories?.ToList() ?? [],
                    PreferredLanguages = appSettings.PreferredLanguages.ToList(),
                    ExistingSeries = ProviderSeriesDetailsResults.Any(a => a.ExistingProvider),
                    DroppedSeries = droppedSeries.ToList()
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AugmentSeriesAsync: {Message}", ex.Message);
                return new AugmentedResponseDto();
            }
        }
    }
}
