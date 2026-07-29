using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Jobs;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Opds;
using RenzoBackend.Services.Settings;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;
using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using ExtensionChapter = Mihon.ExtensionsBridge.Models.Extensions.Chapter;
using ExtensionManga = Mihon.ExtensionsBridge.Models.Extensions.Manga;

namespace RenzoBackend.Services.Series
{
    /// <summary>
    /// Service responsible for series command operations (Create, Update, Delete)
    /// </summary>
    public class SeriesCommandService
    {
        private readonly AppDbContext _db;
        private readonly SettingsService _settings;
        private readonly ArchiveHelperService _archiveHelper;        private readonly SeriesProviderService _providerService;

        private readonly ILogger<SeriesCommandService> _logger;
        private readonly DownloadCommandService _downloadCommand;
        private readonly MihonBridgeService _mihon;
        private readonly ThumbCacheService _cache;
        private readonly JobManagementService _jobManagement;
        private readonly CadenceCalculationService _cadenceService;
        private readonly SeriesStateService _stateService;
        private readonly HashCacheService _hashCache;
        private readonly LockedChapterSupplementService _lockedSupplement;
        private readonly IServiceScopeFactory _scopeFactory;

        private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _memoryCache;

        public SeriesCommandService(AppDbContext db, SettingsService settings, ArchiveHelperService archiveHelper,
            SeriesProviderService providerService, ILogger<SeriesCommandService> logger,
            DownloadCommandService downloadCommand, MihonBridgeService mihon, ThumbCacheService cache,
            JobManagementService jobManagement,
            CadenceCalculationService cadenceService,
            SeriesStateService stateService,
            HashCacheService hashCache,
            LockedChapterSupplementService lockedSupplement,
            IServiceScopeFactory scopeFactory,
            Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache)
        {
            _scopeFactory = scopeFactory;
            _db = db;
            _settings = settings;
            _archiveHelper = archiveHelper;
            _providerService = providerService;
            _logger = logger;
            _downloadCommand = downloadCommand;
            _mihon = mihon;
            _cache = cache;
            _jobManagement = jobManagement;
            _cadenceService = cadenceService;
            _stateService = stateService;
            _hashCache = hashCache;
            _lockedSupplement = lockedSupplement;
            _memoryCache = memoryCache;
          }

        // "Download all" button intent: while set, the GetChapters job for this
        // series queues EVERY missing chapter, ignoring the continue-after cutoff
        // and the global DownloadAllChapters setting. Kept long enough to cover
        // queue latency for all of the series' provider jobs.
        private static string DownloadAllFlagKey(Guid seriesId) => $"dlall:{seriesId}";

        /// <summary>
        /// Adds a new series to the database
        /// </summary>
        /// <param name="ProviderSeriesDetails">Full series information to add</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>The ID of the created series</returns>
        public async Task<Guid> AddSeriesAsync(AugmentedResponseDto ProviderSeriesDetails, Guid ownerId, CancellationToken token = default)
        {
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            if (ProviderSeriesDetails == null || ProviderSeriesDetails.Series.Count == 0)
            {
                throw new ArgumentException("No series provided to add");
            }

            string? ownerUsername = ownerId == Guid.Empty
                ? null
                : await _db.Users.Where(u => u.Id == ownerId).Select(u => u.Username).FirstOrDefaultAsync(token).ConfigureAwait(false);

            using var transaction = await _db.Database.BeginTransactionAsync(token);
            try
            {
                var paths = await _db.GetPathsAsync(token).ConfigureAwait(false);
                string? existingThumb = null;
                List<SeriesProviderEntity> existingProviders = [];
                Models.Database.SeriesEntity? dbSeries = null;
                bool isNewSeries;

                if (ProviderSeriesDetails.ExistingSeriesId.HasValue)
                {
                    dbSeries = await _db.Series.FirstAsync(s => s.Id == ProviderSeriesDetails.ExistingSeriesId, token)
                        .ConfigureAwait(false);
                    if (!SeriesQueryService.CanAccessSeries(dbSeries.OwnerId, ownerId, false))
                        throw new UnauthorizedAccessException("This series belongs to another user's library.");
                    ProviderSeriesDetails.StorageFolderPath = dbSeries.StoragePath;
                    isNewSeries = false;
                }
                else
                {
                    dbSeries = await FindExistingSeriesAsync(ProviderSeriesDetails, settings, paths, ownerId, ownerUsername, token);
                    isNewSeries = dbSeries == null;
                    if (dbSeries != null)
                        existingThumb = dbSeries.ThumbnailUrl;
                }

                if (dbSeries != null)
                {
                    existingProviders = await _db.SeriesProviders.Where(a => a.SeriesId == dbSeries.Id)
                        .ToListAsync(token).ConfigureAwait(false);
                }

                existingProviders = await ProcessSeriesProvidersAsync(ProviderSeriesDetails, existingProviders, token).ConfigureAwait(false);

                // The status source is single-select and must resolve to exactly one
                // provider. Honor an explicit designation from the add UI; otherwise
                // fall back to the permanent (storage) source, then cover, then any —
                // so a newly added series always has a definite status authority.
                NormalizeStatusSource(existingProviders);

                dbSeries = await ConsolidateDBSeriesFromProvidersAsync(dbSeries, existingProviders,
                    ProviderSeriesDetails.StorageFolderPath, ProviderSeriesDetails.DisableJobs, ProviderSeriesDetails.StartChapter, token).ConfigureAwait(false);

                if (isNewSeries)
                    dbSeries.OwnerId = ownerId;

                existingProviders.ForEach(a => a.SeriesId = dbSeries.Id);
                existingProviders.CalculateContinueAfterChapter(ProviderSeriesDetails.StartChapter);
                
                // Populate Pages and PageCount for all chapters from physical archive files
                string seriesBasePath = Path.Combine(settings.StorageFolder, dbSeries.StoragePath);
                foreach (var provider in existingProviders)
                {
                    provider.PopulateChapterPageCounts(seriesBasePath);
                }
                
                await _providerService.CheckIfTheStorageFlagsChangedTheInLibraryStatusOfLastSeriesAsync(
                    existingProviders, [], token).ConfigureAwait(false);
                
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                await transaction.CommitAsync(token).ConfigureAwait(false);
                
                await _providerService.RescheduleIfNeededAsync(existingProviders, true, dbSeries.PauseDownloads, token)
                    .ConfigureAwait(false);
                
                await _stateService.SyncToRenzoJsonAsync(dbSeries.Id, token).ConfigureAwait(false);
                
                if (existingThumb != dbSeries.ThumbnailUrl)
                {
                    await _archiveHelper.WriteComicThumbnailAsync(dbSeries, token).ConfigureAwait(false);
                }

                // Auto-categorize newly added series in the background (MangaDex
                // country-of-origin lookup + relocate if confidently a different
                // bucket). Fire-and-forget with its own scope so it never blocks or
                // fails the add; existing series are handled by the full sweep.
                if (isNewSeries)
                {
                    Guid newId = dbSeries.Id;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var scope = _scopeFactory.CreateScope();
                            var maint = scope.ServiceProvider.GetRequiredService<CategoryMaintenanceService>();
                            await maint.RecategorizeOneAsync(newId, dryRun: false, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception ex) { _logger.LogDebug(ex, "Auto-categorize of new series {Id} failed", newId); }
                    });
                }

                return dbSeries.Id;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in AddSeries: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// Updates an existing series
        /// </summary>
        /// <param name="series">Series information to update</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Updated series extended information</returns>
        public async Task<SeriesExtendedDto> UpdateSeriesAsync(SeriesExtendedDto series, CancellationToken token = default)
        {
            if (series == null || series.Id == Guid.Empty)
            {
                throw new ArgumentException("Invalid series data provided for update");
            }

            Models.Database.SeriesEntity? dbSeries = await _db.Series.Include(s => s.Sources)
                .FirstOrDefaultAsync(s => s.Id == series.Id, token).ConfigureAwait(false);
            if (dbSeries == null)
            {
                throw new KeyNotFoundException($"Series with ID {series.Id} not found");
            }

            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            string existingThumb = dbSeries.ThumbnailUrl;

            // Update provider settings
            UpdateProviderSettings(series, dbSeries);

            List<string> deletedSources = await _providerService.DeleteSourcesIfNeededAsync(series, dbSeries, token)
                .ConfigureAwait(false);
            
            dbSeries = await ConsolidateDBSeriesFromProvidersAsync(dbSeries, dbSeries.Sources.ToList(),
                dbSeries.StoragePath, dbSeries.PauseDownloads, series.StartFromChapter, token);
            
            dbSeries.Sources.CalculateContinueAfterChapter(series.StartFromChapter);
            bool wasPaused = dbSeries.PauseDownloads;
            dbSeries.PauseDownloads = series.PausedDownloads;
            dbSeries.Nsfw = series.Nsfw;
            dbSeries.HideDecimalChapters = series.HideDecimalChapters;
            
            // When series gets paused, clear any queued waiting downloads so they're recalculated on resume
            if (series.PausedDownloads && !wasPaused)
            {
                await _jobManagement.ClearWaitingDownloadsForSeriesAsync(series.Id, token)
                    .ConfigureAwait(false);
            }
            
            _db.Series.Update(dbSeries);
            
            await _providerService.CheckIfTheStorageFlagsChangedTheInLibraryStatusOfLastSeriesAsync(
                dbSeries.Sources, deletedSources, token).ConfigureAwait(false);
            
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            
            await _providerService.RescheduleIfNeededAsync(dbSeries.Sources, true, series.PausedDownloads, token)
                .ConfigureAwait(false);
            
            await _stateService.SyncToRenzoJsonAsync(dbSeries.Id, token).ConfigureAwait(false);
            
            if (existingThumb != dbSeries.ThumbnailUrl)
            {
                await _archiveHelper.WriteComicThumbnailAsync(dbSeries, token).ConfigureAwait(false);
            }

            return dbSeries.ToSeriesExtendedInfo(settings);
        }

        /// <summary>
        /// Deletes a series from the database
        /// </summary>
        /// <param name="id">Series ID to delete</param>
        /// <param name="alsoPhysical">Whether to also delete physical files</param>
        /// <param name="token">Cancellation token</param>
        public async Task DeleteSeriesAsync(Guid id, bool alsoPhysical, CancellationToken token = default)
        {
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            if (id == Guid.Empty)
            {
                throw new ArgumentException("Invalid Series Guid provided for delete");
            }

            Models.Database.SeriesEntity? dbSeries = await _db.Series.Include(s => s.Sources)
                .FirstOrDefaultAsync(s => s.Id == id, token).ConfigureAwait(false);
            if (dbSeries == null)
            {
                throw new KeyNotFoundException($"Series with ID {id} not found");
            }

            List<string> deletedSeries = dbSeries.Sources
                .Select(a => a.MihonId)
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .ToList();
            
            if (alsoPhysical)
                dbSeries.DeletePhysicalSeries(settings, _logger);
            
            foreach (SeriesProviderEntity p in dbSeries.Sources)
            {
                await _providerService.RescheduleIfNeededAsync([p], false, true, token).ConfigureAwait(false);
            }

            _db.Series.Remove(dbSeries);
            
            await _providerService.CheckIfTheStorageFlagsChangedTheInLibraryStatusOfLastSeriesAsync(
                [], deletedSeries, token).ConfigureAwait(false);
            
            await _db.SaveChangesAsync(token).ConfigureAwait(false);
        }

        


        /// <summary>
        /// Updates a source with latest series information (moved from SeriesUpdateService)
        /// </summary>
        public async Task<JobResult> UpdateSourceAsync(string mihonProviderId, CancellationToken token)
        {
            try
            {
                Dictionary<string, (DateTime, Manga?, ParsedChapter?)> latestDates = await _db.LatestSeries.Where(a => a.MihonProviderId == mihonProviderId).ToDictionaryAsync(a => a.MihonId, a => (a.FetchDate, a.ToManga(), a.Chapters.OrderByDescending(b => b.Index).FirstOrDefault()), token).ConfigureAwait(false);
                ConcurrentDictionary<string, ComboSeries> newChaps = [];
                int page = 1;
                bool upToDate = false;
                bool neverDone = latestDates.Count == 0;
                ISourceInterop src;
                try
                {
                    src = await _mihon.SourceFromProviderIdAsync(mihonProviderId, token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Unable to get Latest Series from {mihonProviderId}", mihonProviderId);
                    return JobResult.Failed;
                }
                string provider = src.Name + " (" + src.Language + ")";
                // Sources without a "latest updates" listing fall back to the
                // popular listing so their catalog still reaches Browse.
                bool useLatest = src.SupportsLatest;
                // First run for a source: walk a few pages so a freshly added
                // source doesn't sit nearly empty until the next scheduled runs.
                const int FirstRunPages = 3;
                bool hasNext = true;
                _logger.LogInformation("Updating {listing} Series from Provider {provider}...", useLatest ? "Latest" : "Popular", provider);
                do
                {
                    MangaList? res;
                    res = await _mihon.MihonErrorWrapperAsync(
                        () => useLatest ? src.GetLatestAsync(page, token) : src.GetPopularAsync(page, token),
                        "Unable to get Latest Series from {provider}", provider).ConfigureAwait(false);
                    if (res==null)
                        return JobResult.Failed;
                    hasNext = res.HasNextPage;

                    SettingsDto s = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
                    await Parallel.ForEachAsync(res.Mangas, new ParallelOptions
                    {
                        CancellationToken = token,
                        MaxDegreeOfParallelism = s.NumberOfSimultaneousDownloadsPerProvider
                    },
                        async (ss, b) =>
                        {
                            if (upToDate)
                                return;
                            ComboSeries s = new ComboSeries();
                            string mihonId = mihonProviderId + "|" + ss.Url;
                            s.MihonId= mihonId;
                            if (!latestDates.TryGetValue(mihonId, out (DateTime, Manga?, ParsedChapter?) value) ||
                                (value.Item1.AddDays(7) < DateTime.UtcNow))
                            {
                                s.Series = await _mihon.MihonErrorWrapperAsync(
                                    () => src.GetDetailsAsync(ss, token),
                                    "Unable to get Series {Title} from {provider}", ss.Title, provider).ConfigureAwait(false);
                                if (s.Series == null)
                                    return;
                                newChaps[mihonId] = s;
                            }

                            List<ParsedChapter>? chaps = await _mihon.MihonErrorWrapperAsync(
                                () => src.GetChaptersAsync(ss, token),
                                "Unable to get Series {Title} Chapters from {provider}", ss.Title, provider).ConfigureAwait(false);
                            if (chaps == null)
                            {
                                newChaps.Remove(mihonId, out _);
                                return;
                            }

                            s.Chapters = chaps;
                            ParsedChapter? latest_online = chaps.OrderByDescending(a => a.Index).FirstOrDefault();
                            if (latest_online != null && latestDates.TryGetValue(mihonId, out (DateTime, Manga?, ParsedChapter?) value2) && value2.Item2 != null && value2.Item3!=null)
                            {
                                if ((latestDates[mihonId].Item3!.Index >= latest_online.Index) &&
                                    (latestDates[mihonId].Item3!.DateUpload >= latest_online.DateUpload))
                                {
                                    upToDate = true;
                                }
                            }
                        }).ConfigureAwait(false);
                    if (upToDate)
                        break;
                    page++;
                    // Established latest-capable sources page until they hit known
                    // territory; popular-fallback sources take one page per run;
                    // a source's very first run walks up to FirstRunPages pages.
                } while (!upToDate && hasNext && (neverDone ? page <= FirstRunPages : useLatest));

                List<string> ids = newChaps.Keys.ToList();
                List<LatestSerieEntity> toUpdate = await _db.LatestSeries.Where(a => ids.Contains(a.MihonId)).ToListAsync(token).ConfigureAwait(false);
                List<(LatestSerieEntity, SeriesProviderEntity)> toCheck = [];

                foreach (ComboSeries c in newChaps.Values)
                {
                    LatestSerieEntity? s = toUpdate.FirstOrDefault(a => a.MihonId == c.MihonId);
                    if (s == null)
                    {
                        s = new LatestSerieEntity();
                        s.MihonId = c.MihonId;
                        s.MihonProviderId = mihonProviderId;
                        _db.LatestSeries.Add(s);
                    }
                    if (c.Series != null)
                    {
                        await s.PopulateSeriesAsync(src, c.Series, _cache).ConfigureAwait(false);
                    }
                    s.Chapters = c.Chapters;
                    ParsedChapter? latest_online = s.Chapters.OrderByDescending(a => a.Index).FirstOrDefault();
                    DateTime latestUTC = latest_online?.DateUpload.DateTime ?? DateTime.MinValue;

                    if (latestUTC > DateTime.UtcNow || latestUTC.AddMonths(1) < DateTime.UtcNow)
                    {
                        latestUTC = DateTime.UtcNow;
                    }
                    s.FetchDate = latestUTC;
                    s.LatestChapter = latest_online?.ParsedNumber ?? -1.0m;
                    s.ChapterCount = s.Chapters.Count;
                    s.LatestChapterTitle = latest_online?.Name ?? "";
                    SeriesProviderEntity? serie = await _db.SeriesProviders
                        .Where(a => a.MihonId == s.MihonId).AsNoTracking()
                        .FirstOrDefaultAsync(token).ConfigureAwait(false);
                    s.InLibrary = InLibraryStatus.NotInLibrary;
                    if (serie != null)
                    {
                        s.SeriesId = serie.SeriesId;
                        if (serie.IsDisabled || serie.IsUninstalled)
                            s.InLibrary = InLibraryStatus.InLibraryButDisabled;
                        else
                        {
                            toCheck.Add((s, serie));
                            s.InLibrary = InLibraryStatus.InLibrary;
                        }
                    }
                }
                await _db.SaveChangesAsync(token).ConfigureAwait(false);

                bool downloadAllLatest = (await _settings.GetSettingsAsync(token).ConfigureAwait(false)).DownloadAllChapters;
                foreach (var u in toCheck)
                {
                    Models.Database.SeriesEntity series = await _db.Series.Include(a => a.Sources)
                        .Where(a => a.Id == u.Item2.SeriesId).AsNoTracking().FirstAsync(token).ConfigureAwait(false);
                    if (!series.PauseDownloads)
                    {
                        List<ChapterDownload> chaps = series.GenerateDownloadsFromChapterData(u.Item2, u.Item1.Chapters, downloadAllLatest);
                        if (chaps.Count > 0)
                        {
                            await _downloadCommand.QueueChapterDownloadsAsync(u.Item2, chaps, token).ConfigureAwait(false);
                        }
                    }
                }
                _logger.LogInformation("Latest Series update from Provider {provider} complete.", provider);

                return JobResult.Success;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error Updating Source : {Message}", e.Message);
                return JobResult.Failed;
            }
        }


        /// <summary>
        /// Downloads/updates a specific series provider (moved from SeriesUpdateService)
        /// </summary>
        public async Task<JobResult> GetChaptersAsync(Guid seriesProvider, CancellationToken token = default)
        {
            // Load TRACKING entities (no AsNoTracking) so we can save error/refresh data
            SeriesProviderEntity? serie = await _db.SeriesProviders.FirstOrDefaultAsync(s => s.Id == seriesProvider, token).ConfigureAwait(false);
            if (serie == null)
            {
                _logger.LogWarning("Series Provider {SeriesProvider} no longer exists", seriesProvider);
                return JobResult.Delete;
            }
            if (serie.IsDisabled || serie.IsUninstalled)
            {
                _logger.LogWarning("Series Provider {SeriesProvider} is disabled or uninstalled", seriesProvider);
                return JobResult.Failed;
            }
            if (string.IsNullOrEmpty(serie.MihonProviderId))
            {
                _logger.LogWarning("Series Provider {SeriesProvider} has no longer valid Mihon Id; deleting job", seriesProvider);
                return JobResult.Delete;
            }

            Models.Database.SeriesEntity? series = await _db.Series.Include(a => a.Sources)
                .FirstOrDefaultAsync(s => s.Id == serie.SeriesId, token).ConfigureAwait(false);
            if (series == null)
            {
                _logger.LogWarning("Series {SeriesId} for Provider {SeriesProvider} not found", serie.SeriesId, seriesProvider);
                return JobResult.Delete;
            }

            ISourceInterop src;
            try
            {
                src = await _mihon.SourceFromProviderIdAsync(serie.MihonProviderId!, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to get Chapter from {mihonProviderId}", serie.MihonProviderId);
                // Track the error on the provider
                serie.LastErrorDate = DateTime.UtcNow;
                serie.ConsecutiveErrorCount++;
                _db.Touch(serie, a => a.LastErrorDate);
                _db.Touch(serie, a => a.ConsecutiveErrorCount);
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                return JobResult.Failed;
            }
            
            string provider = src.Name + " (" + src.Language + ")";
            _logger.LogInformation("Getting chapters from Series {series} Provider {provider}", serie.Title, provider);
            List<ParsedChapter>? chapterData;
            try
            {
                chapterData = await _mihon.MihonErrorWrapperAsync(
                    () => src.GetChaptersAsync(serie.ToManga()!, token),
                    "Unable to get Chapters from {series} from {provider}", serie.Title, provider).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Track the error on the provider
                serie.LastErrorDate = DateTime.UtcNow;
                serie.ConsecutiveErrorCount++;
                _db.Touch(serie, a => a.LastErrorDate);
                _db.Touch(serie, a => a.ConsecutiveErrorCount);
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                return JobResult.Failed;
            }

            if (chapterData == null || chapterData.Count == 0)
            {
                _logger.LogWarning("Series {series} from Provider {provider} has no chapters.", serie.Title, provider);
                // If chapters returned empty, it might still be a valid state but we shouldn't flag as error
                // Only track as error if it was a connection/parsing failure, not empty results
                return JobResult.Failed;
            }

            // Success — reset error tracking
            serie.ConsecutiveErrorCount = 0;
            serie.LastSuccessfulFetchDate = DateTime.UtcNow;
            _db.Touch(serie, a => a.ConsecutiveErrorCount);
            _db.Touch(serie, a => a.LastSuccessfulFetchDate);

            // Refresh series metadata (status, description, etc.) from the extension
            try
            {
                var extensionManga = await _mihon.MihonErrorWrapperAsync(
                    () => src.GetDetailsAsync(serie.ToManga()!, token),
                    "Unable to get Details from {series} from {provider}", serie.Title, provider).ConfigureAwait(false);

                if (extensionManga != null)
                {
                    SeriesStatus newStatus = (SeriesStatus)(int)extensionManga.Status;
                    bool statusChanged = newStatus != serie.LastKnownStatus;

                    // Only the designated status authority drives the SERIES-level
                    // status, so one source flipping to "completed" (or a scanlator
                    // marking "cancelled") doesn't overwrite it. Authority order:
                    // the explicit status source, else the permanent (storage)
                    // source, else — if nothing is designated — any source.
                    bool anyStatusSource = series.Sources.Any(a => a.IsStatus);
                    bool anyPermanent = series.Sources.Any(a => a.IsStorage);
                    bool isStatusAuthority = serie.IsStatus
                        || (!anyStatusSource && serie.IsStorage)
                        || (!anyStatusSource && !anyPermanent);

                    // Update the series-level metadata
                    if (!string.IsNullOrEmpty(extensionManga.Title))
                        series.Title = extensionManga.Title;
                    if (!string.IsNullOrEmpty(extensionManga.Artist))
                        series.Artist = extensionManga.Artist;
                    if (!string.IsNullOrEmpty(extensionManga.Author))
                        series.Author = extensionManga.Author;
                    if (!string.IsNullOrEmpty(extensionManga.Description))
                        series.Description = extensionManga.Description;
                    if (!string.IsNullOrEmpty(extensionManga.Genre))
                        series.Genre = extensionManga.Genre.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                    if (isStatusAuthority)
                        series.Status = newStatus;

                    // Update provider-level metadata
                    serie.Status = newStatus;
                    serie.LastKnownStatus = newStatus;
                    serie.LastSeriesInfoRefreshDate = DateTime.UtcNow;
                    _db.Touch(serie, a => a.Status);
                    _db.Touch(serie, a => a.LastKnownStatus);
                    _db.Touch(serie, a => a.LastSeriesInfoRefreshDate);

                    // If the series was completed/cancelled/hiatus, clear any active health alert
                    if (newStatus == SeriesStatus.COMPLETED ||
                        newStatus == SeriesStatus.CANCELLED ||
                        newStatus == SeriesStatus.ON_HIATUS ||
                        newStatus == SeriesStatus.PUBLISHING_FINISHED)
                    {
                        var existingAlert = await _db.HealthStatuses
                            .FirstOrDefaultAsync(h => h.TargetType == HealthStatusTargetType.Series
                                && h.TargetId == series.Id && h.IsActive, token).ConfigureAwait(false);
                        if (existingAlert != null)
                        {
                            existingAlert.IsActive = false;
                            existingAlert.ResolvedAt = DateTime.UtcNow;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Metadata refresh is best-effort; don't fail the entire chapter fetch
                _logger.LogWarning(ex, "Failed to refresh metadata for {series} from {provider}", serie.Title, provider);
            }

            // Update LastChapterDate on the series entity
            if (chapterData.Count > 0)
            {
                DateTime? latestChapterDate = chapterData
                    .Where(c => c.DateUpload != default)
                    .Max(c => c.DateUpload.DateTime);

                if (latestChapterDate.HasValue)
                {
                    if (series.LastChapterDate == null || latestChapterDate > series.LastChapterDate)
                    {
                        series.LastChapterDate = latestChapterDate;
                    }
                }
            }

            // Merge discovered chapters into the DB AT SCAN TIME. Previously a
            // chapter only entered the DB when its download completed, so anything
            // that didn't download (locked/paid early-access, paused series, failed
            // fetches) was invisible — chapter counts never refreshed and locked
            // chapters couldn't show their lock. Now every scan makes discoveries
            // visible immediately (as "missing" until downloaded), with the found
            // time stamped for the Updates feed.
            {
                // Same scanlator scoping the download path uses.
                IEnumerable<ParsedChapter> pool = chapterData;
                chapterData.ForEach(a => { if (string.IsNullOrEmpty(a.Scanlator)) a.Scanlator = serie.Provider; });
                if (serie.Scanlator == serie.Provider || string.IsNullOrEmpty(serie.Scanlator))
                    pool = pool.Where(a => string.IsNullOrEmpty(a.Scanlator) || a.Scanlator == serie.Provider);
                else
                    pool = pool.Where(a => a.Scanlator == serie.Scanlator);

                bool established = serie.Chapters.Count > 0;
                DateTime foundNow = DateTime.UtcNow;
                bool chaptersChanged = false;
                foreach (ParsedChapter pc in pool)
                {
                    Models.Chapter? existing = serie.Chapters.FirstOrDefault(a => a.Number == pc.ParsedNumber);
                    if (existing == null)
                    {
                        Models.Chapter nc = pc.ToChapter();
                        nc.DateFetched = established
                            ? foundNow
                            : (RenzoBackend.Extensions.ModelExtensions.HasRealUploadDate(nc.ProviderUploadDate) ? nc.ProviderUploadDate : null);
                        nc.ShouldDownload = serie.ContinueAfterChapter < nc.Number || serie.ContinueAfterChapter == null;
                        serie.Chapters.Add(nc);
                        chaptersChanged = true;
                    }
                    else if (string.IsNullOrEmpty(existing.Url) && !string.IsNullOrEmpty(pc.RealUrl))
                    {
                        existing.Url = pc.RealUrl; // backfill the purchase/source link
                        chaptersChanged = true;
                    }
                }
                // Supplement with coin-gated chapters the extension can't see, but
                // only when the owner has a login for this source (a paid site) —
                // no point hitting FlareSolverr for a source with no paywall/login.
                if (await OwnerHasSiteLoginAsync(series.OwnerId, serie.Provider, token).ConfigureAwait(false))
                {
                    // Any chapter-looking URL will do — the supplement service
                    // fingerprints the platform itself and safely returns nothing
                    // for a shape it doesn't handle. Requiring the WordPress
                    // "{slug}-chapter-N" form here meant sites that put the
                    // chapter in its own path segment ("/series/{slug}/chapter-N",
                    // e.g. Magus Manga) never even reached the supplement, so
                    // their paid chapters silently never appeared.
                    string? sampleUrl = serie.Chapters
                        .Select(c => c.Url)
                        .FirstOrDefault(u => !string.IsNullOrEmpty(u) && u!.Contains("chapter", StringComparison.OrdinalIgnoreCase));
                    if (sampleUrl != null)
                    {
                        var existingNums = serie.Chapters.Where(c => c.Number != null).Select(c => c.Number!.Value).ToList();
                        List<Models.Chapter> locked = await _lockedSupplement
                            .FetchLockedChaptersAsync(sampleUrl, existingNums, token).ConfigureAwait(false);
                        foreach (Models.Chapter lc in locked)
                        {
                            serie.Chapters.Add(lc);
                            chaptersChanged = true;
                        }
                    }
                }

                if (chaptersChanged)
                {
                    serie.ChapterCount = serie.Chapters.Count;
                    _db.Touch(serie, a => a.Chapters);
                }
            }

            await _db.SaveChangesAsync(token).ConfigureAwait(false);

            // Recalculate release cadence after fetching new chapters
            await _cadenceService.RecalculateCadenceAsync(series.Id, token).ConfigureAwait(false);

            // Sync renzo.json after metadata refresh (series.Title, Artist, etc. may have changed)
            await _stateService.SyncToRenzoJsonAsync(series.Id, token).ConfigureAwait(false);

            // Respect the series-level pause as the source of truth for downloads.
            // Metadata above is always refreshed (status drives alerts), but no chapters are
            // queued while paused. Pause is normally enforced by disabling the recurring job,
            // however some paths re-run this job with the job enabled (e.g. extension
            // (re)install/update reschedules providers with forceDisable=false), which would
            // otherwise bypass the pause flag — guarding here closes every such path.
            if (series.PauseDownloads)
            {
                _logger.LogInformation("Series {series} is paused; metadata refreshed but skipping chapter downloads", serie.Title);
                return JobResult.Success;
            }

            bool downloadAll = (await _settings.GetSettingsAsync(token).ConfigureAwait(false)).DownloadAllChapters
                || _memoryCache.TryGetValue(DownloadAllFlagKey(series.Id), out _);
            bool allowLocked = await OwnerHasSiteLoginAsync(series.OwnerId, serie.Provider, token).ConfigureAwait(false);
            List<ChapterDownload> chaps = series.GenerateDownloadsFromChapterData(serie, chapterData, downloadAll, allowLocked);

            // Respect the series pause flag — don't queue downloads when paused
            if (!series.PauseDownloads)
            {
                return await _downloadCommand.QueueChapterDownloadsAsync(serie, chaps, token).ConfigureAwait(false);
            }

            return JobResult.Success;
        }

        /// <summary>
        /// Triggers an immediate refresh for a single series: re-fetches metadata (status, title,
        /// cover, description, etc.) and checks for new chapters by enqueuing the GetChapters job
        /// for each active provider. Honors the series pause flag (paused series refresh metadata
        /// but do not download — enforced inside <see cref="GetChaptersAsync"/>).
        /// </summary>
        /// <param name="seriesId">The series to refresh.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>The number of providers queued for refresh.</returns>
        public async Task<int> RefreshSeriesMetadataAsync(Guid seriesId, CancellationToken token = default)
            => await RefreshSeriesMetadataAsync(seriesId, null, token).ConfigureAwait(false);

        /// <summary>
        /// Same as <see cref="RefreshSeriesMetadataAsync(Guid, CancellationToken)"/> but,
        /// when <paramref name="onlyIfOlderThan"/> is given, providers fetched more
        /// recently than that are skipped. Used by the open-a-series auto-refresh so
        /// clicking around the library doesn't hammer sources.
        /// </summary>
        public async Task<int> RefreshSeriesMetadataAsync(Guid seriesId, TimeSpan? onlyIfOlderThan, CancellationToken token = default)
        {
            List<SeriesProviderEntity> providers = await _db.SeriesProviders
                .Where(p => p.SeriesId == seriesId && !p.IsUnknown && !p.IsLocal
                    && !p.IsDisabled && !p.IsUninstalled)
                .ToListAsync(token).ConfigureAwait(false);

            DateTime? staleBefore = onlyIfOlderThan == null ? null : DateTime.UtcNow - onlyIfOlderThan.Value;
            int queued = 0;
            foreach (SeriesProviderEntity p in providers)
            {
                if (string.IsNullOrEmpty(p.MihonProviderId))
                    continue;
                if (staleBefore != null && p.LastSuccessfulFetchDate != null && p.LastSuccessfulFetchDate > staleBefore)
                    continue; // fresh enough — don't re-hit the source
                await _jobManagement.EnqueueJobAsync(JobType.GetChapters, p.Id, Priority.High,
                    key: p.Id.ToString(), token: token).ConfigureAwait(false);
                queued++;
            }

            if (queued > 0)
                _logger.LogInformation("Queued metadata refresh for {count} provider(s) of series {seriesId}",
                    queued, seriesId);
            return queued;
        }

        /// <summary>
        /// Library-wide new-chapter scan: enqueues the GetChapters job for every
        /// active provider of every series. Backs the "Update now" button and the
        /// rolling scan; per-provider dedup in the queue means overlapping scans
        /// don't double-fetch.
        /// </summary>
        public async Task<JobResult> ScanAllSeriesAsync(CancellationToken token = default)
        {
            List<SeriesProviderEntity> providers = await _db.SeriesProviders
                .Where(p => !p.IsUnknown && !p.IsLocal && !p.IsDisabled && !p.IsUninstalled
                    && p.MihonProviderId != null && p.MihonProviderId != "")
                .ToListAsync(token).ConfigureAwait(false);

            int queued = 0;
            foreach (SeriesProviderEntity p in providers)
            {
                token.ThrowIfCancellationRequested();
                await _jobManagement.EnqueueJobAsync(JobType.GetChapters, p.Id, Priority.Normal,
                    key: p.Id.ToString(), token: token).ConfigureAwait(false);
                queued++;
            }
            _logger.LogInformation("Library scan queued a chapter check for {count} provider(s).", queued);
            return JobResult.Success;
        }

        /// <summary>
        /// Queues a download of every not-yet-downloaded chapter for the series by enqueuing a
        /// chapter fetch+download for each active provider. The download job only queues chapters
        /// that are missing from disk, so already-downloaded chapters are left untouched. Honors the
        /// series pause flag.
        /// </summary>
        /// <param name="seriesId">The series to fill in.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Number of providers queued, or -1 if the series is paused, -2 if it doesn't exist.</returns>
        public async Task<int> QueueDownloadAllAsync(Guid seriesId, CancellationToken token = default)
        {
            Models.Database.SeriesEntity? series = await _db.Series
                .Include(s => s.Sources)
                .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
            if (series == null)
                return -2;
            if (series.PauseDownloads)
                return -1;

            // Mark the intent so any (re)fetch this triggers also queues every
            // missing chapter, not just those past the continue-after cutoff,
            // regardless of the global DownloadAllChapters setting.
            _memoryCache.Set(DownloadAllFlagKey(seriesId), true, TimeSpan.FromHours(1));

            // Queue every chapter the DB ALREADY knows is missing, directly and
            // immediately — don't wait on a fresh source fetch. Previously this only
            // triggered a metadata refresh, so a chapter that's missing on disk but
            // no longer in what the source currently returns (older chapters routinely
            // drop off a source's listing), or whose provider's GetChapters job was
            // deduped by an in-flight library scan, would never get queued and the
            // button appeared to do nothing. GenerateDownloadsFromChapterData applies
            // the same exists/scanlator/locked filters as the fetch path, and
            // QueueChapterDownloadsAsync dedups by chapter, so this is safe to run
            // alongside the refresh below.
            int queuedDownloads = 0;
            foreach (SeriesProviderEntity p in series.Sources.Where(p =>
                         !p.IsUnknown && !p.IsLocal && !p.IsDisabled && !p.IsUninstalled
                         && !string.IsNullOrEmpty(p.MihonProviderId)))
            {
                List<ParsedChapter> known = p.Chapters
                    .Where(c => c.Number != null)
                    .Select(c => ChapterToParsedChapter(c, p))
                    .ToList();
                if (known.Count == 0)
                    continue;
                bool allowLocked = await OwnerHasSiteLoginAsync(series.OwnerId, p.Provider, token).ConfigureAwait(false);
                List<ChapterDownload> chaps = series.GenerateDownloadsFromChapterData(p, known, downloadAll: true, allowLocked: allowLocked);
                if (chaps.Count > 0)
                {
                    await _downloadCommand.QueueChapterDownloadsAsync(p, chaps, token).ConfigureAwait(false);
                    queuedDownloads += chaps.Count;
                }
            }

            // Also refresh metadata so brand-new chapters the DB doesn't know yet get
            // discovered and queued (the flag above makes that fetch download-all too).
            int refreshed = await RefreshSeriesMetadataAsync(seriesId, token).ConfigureAwait(false);
            _logger.LogInformation("Download-all for series {SeriesId}: queued {Queued} known-missing chapter(s), refreshing {Providers} provider(s).",
                seriesId, queuedDownloads, refreshed);

            return queuedDownloads;
        }

        /// <summary>
        /// Rebuilds a source-shaped ParsedChapter from a stored DB chapter so the
        /// existing download-generation path can be reused without re-fetching the
        /// source. The download job later pulls the actual pages from the chapter URL.
        /// </summary>
        /// <summary>
        /// Ensures exactly one non-uninstalled provider is the status authority:
        /// keeps an explicit designation if present, else picks the permanent
        /// (storage) source, else cover, else the first. Mirrors the update-flow
        /// normalization so the add flow and edit flow agree.
        /// </summary>
        private static void NormalizeStatusSource(List<SeriesProviderEntity> providers)
        {
            var sources = providers.Where(a => !a.IsUninstalled).ToList();
            if (sources.Count == 0)
                return;
            SeriesProviderEntity? chosen = sources.FirstOrDefault(a => a.IsStatus)
                ?? sources.FirstOrDefault(a => a.IsStorage)
                ?? sources.FirstOrDefault(a => a.IsCover)
                ?? sources.FirstOrDefault();
            foreach (SeriesProviderEntity a in sources)
                a.IsStatus = ReferenceEquals(a, chosen);
        }

        /// <summary>
        /// True when an active site login exists for a source, so its paid/locked
        /// chapters are actually reachable and worth queuing. Scoped to the series
        /// owner (or any user for legacy unowned series). "ok" = auto-login worked,
        /// "manual_cookie" = a pasted session cookie is in the jar.
        /// </summary>
        private async Task<bool> OwnerHasSiteLoginAsync(Guid ownerId, string provider, CancellationToken token)
        {
            if (string.IsNullOrEmpty(provider))
                return false;
            return await _db.SiteCredentials.AnyAsync(c =>
                c.Provider == provider
                && (c.Status == "ok" || c.Status == "manual_cookie")
                && (ownerId == Guid.Empty || c.UserId == ownerId),
                token).ConfigureAwait(false);
        }

        private static ParsedChapter ChapterToParsedChapter(Models.Chapter c, SeriesProviderEntity p)
        {
            decimal num = c.Number ?? 0m;
            return new ParsedChapter
            {
                // Models.Chapter.Url is always the ABSOLUTE url (ModelExtensions.ToChapter
                // sets it from ParsedChapter.RealUrl, never the source-relative .Url) — but
                // ParsedChapter.Url here becomes SChapter.url, which HttpSource extensions
                // treat as relative to their own baseUrl. Passing the absolute value through
                // unchanged made the source request baseUrl+absoluteUrl, a malformed,
                // always-failing double-domain URL — this is why "Download All" re-queuing a
                // known-but-undownloaded chapter (including a just-purchased locked one)
                // could resolve the chapter but never actually fetch its pages.
                Url = ToRelativeUrl(c.Url),
                RealUrl = c.Url ?? string.Empty,
                Name = c.Name ?? string.Empty,
                ParsedName = c.Name ?? string.Empty,
                ChapterNumber = (float)num,
                ParsedNumber = num,
                Index = c.ProviderIndex,
                Scanlator = string.IsNullOrEmpty(p.Scanlator) ? p.Provider : p.Scanlator,
                DateUpload = c.ProviderUploadDate.HasValue
                    ? new DateTimeOffset(DateTime.SpecifyKind(c.ProviderUploadDate.Value, DateTimeKind.Utc))
                    : DateTimeOffset.UtcNow,
            };
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

        /// <summary>
        /// Deletes downloaded chapter files for a series. When <paramref name="chapterNumbers"/> is
        /// null, every downloaded chapter is removed; otherwise only the listed chapter numbers.
        /// Removes the file(s) held by any source, clears each chapter's download state, and prunes
        /// the hash cache. Metadata rows stay — the chapter can be re-downloaded later.
        /// </summary>
        /// <param name="seriesId">The series to delete downloads for.</param>
        /// <param name="chapterNumbers">Chapter numbers to delete, or null for all downloaded.</param>
        /// <param name="token">Cancellation token.</param>
        /// <returns>Count of chapters whose file was removed, or -2 if the series doesn't exist.</returns>
        public async Task<int> DeleteDownloadsAsync(Guid seriesId, IReadOnlyCollection<decimal>? chapterNumbers,
            CancellationToken token = default)
        {
            Models.Database.SeriesEntity? series = await _db.Series.Include(s => s.Sources)
                .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
            if (series == null)
                return -2;

            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            HashSet<decimal>? wanted = chapterNumbers is { Count: > 0 }
                ? new HashSet<decimal>(chapterNumbers) : null;

            var clearedNumbers = new HashSet<decimal>();
            foreach (SeriesProviderEntity sp in series.Sources)
            {
                bool touched = false;
                foreach (Models.Chapter c in sp.Chapters)
                {
                    if (string.IsNullOrEmpty(c.Filename) || c.Number == null)
                        continue;
                    if (wanted != null && !wanted.Contains(c.Number.Value))
                        continue;

                    _hashCache.DeleteChapterHash(series.StoragePath, c.Filename!);
                    string full = Path.Combine(settings.StorageFolder, series.StoragePath, c.Filename!);
                    if (File.Exists(full))
                    {
                        try { File.Delete(full); }
                        catch (Exception e) { _logger.LogWarning(e, "Unable to delete file {full}", full); }
                    }
                    c.Filename = null;
                    c.DownloadDate = null;
                    // Deleting is a deliberate removal — don't let the next scan immediately
                    // re-queue it. It stays available to re-download on demand.
                    c.ShouldDownload = false;
                    clearedNumbers.Add(c.Number.Value);
                    touched = true;
                }
                if (touched)
                    _db.Touch(sp, a => a.Chapters);
            }

            if (clearedNumbers.Count > 0)
            {
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                await _stateService.SyncToRenzoJsonAsync(series.Id, token).ConfigureAwait(false);
                _logger.LogInformation("Deleted {Count} downloaded chapter(s) for series {Series}.",
                    clearedNumbers.Count, series.Title);
            }
            return clearedNumbers.Count;
        }

        /// <summary>
        /// Re-downloads (or downloads) a single chapter, replacing any existing file on disk. The
        /// target source is resolved by priority — the storage source that offers the chapter, then
        /// the source currently holding the file, then any other remote-capable source — unless an
        /// explicit <paramref name="providerId"/> override is supplied. Honors the series pause flag
        /// (paused series cannot re-download). Bypasses the bulk "already downloaded" filters so an
        /// existing chapter is genuinely re-fetched.
        /// </summary>
        /// <param name="seriesId">The series owning the chapter.</param>
        /// <param name="chapterNumber">The chapter number to (re-)download.</param>
        /// <param name="providerId">Optional explicit source to force; null = priority default.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task<RedownloadResult> RedownloadChapterAsync(Guid seriesId, decimal chapterNumber,
            Guid? providerId = null, CancellationToken token = default)
        {
            Models.Database.SeriesEntity? series = await _db.Series.Include(a => a.Sources)
                .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
            if (series == null)
                return new RedownloadResult(RedownloadOutcome.SeriesNotFound);

            // Pause is authoritative — block explicit re-downloads while the series is paused.
            if (series.PauseDownloads)
                return new RedownloadResult(RedownloadOutcome.Paused);

            bool HasChapter(SeriesProviderEntity p) =>
                p.Chapters.Any(c => !c.IsDeleted && c.Number == chapterNumber);
            bool Capable(SeriesProviderEntity p) =>
                !p.IsUnknown && !p.IsLocal && !p.IsDisabled && !p.IsUninstalled
                && !string.IsNullOrEmpty(p.MihonProviderId);

            SeriesProviderEntity? target;
            if (providerId.HasValue)
            {
                target = series.Sources.FirstOrDefault(p => p.Id == providerId.Value);
                if (target == null || !Capable(target))
                    return new RedownloadResult(RedownloadOutcome.NoSourceAvailable);
            }
            else
            {
                // Highest per-series Priority (0 = top) wins, then storage, then a source that already
                // holds the file, then any capable. This makes the re-download come from the user's
                // preferred source for the series' chapter quality.
                List<SeriesProviderEntity> candidates = series.Sources
                    .Where(p => Capable(p) && HasChapter(p))
                    .OrderBy(p => p.Priority)
                    .ThenByDescending(p => p.IsStorage)
                    .ToList();
                target = candidates.FirstOrDefault();
                if (target == null)
                    return new RedownloadResult(RedownloadOutcome.NoSourceAvailable);
            }

            // Re-fetch the live chapter list so the download uses a fresh URL.
            ISourceInterop src;
            try
            {
                src = await _mihon.SourceFromProviderIdAsync(target.MihonProviderId!, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to resolve source for provider {Provider}", target.Provider);
                return new RedownloadResult(RedownloadOutcome.NoSourceAvailable);
            }

            List<ParsedChapter>? chapterData = await _mihon.MihonErrorWrapperAsync(
                () => src.GetChaptersAsync(target.ToManga()!, token),
                "Unable to get Chapters from {series} from {provider}", series.Title, target.Provider).ConfigureAwait(false);

            ParsedChapter? match = null;
            if (chapterData is { Count: > 0 })
            {
                // Apply the same scanlator scoping the bulk download path uses.
                chapterData.ForEach(a =>
                {
                    if (string.IsNullOrEmpty(a.Scanlator))
                        a.Scanlator = target.Provider;
                });
                IEnumerable<ParsedChapter> pool = chapterData;
                if (target.Scanlator == target.Provider || string.IsNullOrEmpty(target.Scanlator))
                    pool = pool.Where(a => string.IsNullOrEmpty(a.Scanlator) || a.Scanlator == target.Provider);
                else
                    pool = pool.Where(a => a.Scanlator == target.Scanlator);

                match = pool.FirstOrDefault(c => c.ParsedNumber == chapterNumber);
            }

            // Paid/coin-gated chapters routinely disappear from (or get renamed in) a
            // source's live listing once purchased — the same reason a plain reader
            // fetch used to 404 on an already-unlocked chapter (see ReaderPreviewService).
            // "Download All" already tolerates this by building straight from the DB's
            // stored Chapter row instead of requiring a live match; do the same here
            // rather than failing an explicit re-download of a chapter the user owns.
            if (match == null)
            {
                Models.Chapter? known = target.Chapters.FirstOrDefault(c => !c.IsDeleted && c.Number == chapterNumber);
                if (known != null)
                    match = ChapterToParsedChapter(known, target);
            }
            if (match == null)
                return new RedownloadResult(RedownloadOutcome.ChapterNotFound);

            // Remove any existing on-disk copy of this chapter (held by whichever source) and reset
            // its row, so the fresh download replaces it instead of leaving an orphan or duplicate.
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            bool cleared = false;
            foreach (SeriesProviderEntity sp in series.Sources)
            {
                foreach (Models.Chapter c in sp.Chapters.Where(c => c.Number == chapterNumber && !string.IsNullOrEmpty(c.Filename)))
                {
                    _hashCache.DeleteChapterHash(series.StoragePath, c.Filename!);
                    string full = Path.Combine(settings.StorageFolder, series.StoragePath, c.Filename!);
                    if (File.Exists(full))
                    {
                        try { File.Delete(full); }
                        catch (Exception e) { _logger.LogWarning(e, "Unable to delete file {full} for re-download", full); }
                    }
                    c.Filename = null;
                    c.DownloadDate = null;
                    c.ShouldDownload = true;
                    _db.Touch(sp, a => a.Chapters);
                    cleared = true;
                }
            }
            if (cleared)
            {
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
                await _stateService.SyncToRenzoJsonAsync(series.Id, token).ConfigureAwait(false);
            }

            // Build a single targeted download, bypassing the bulk "already downloaded" filters.
            List<ChapterDownload> chaps = series.ToDownloads(target, new List<ParsedChapter> { match }, series.StoragePath);
            await _downloadCommand.QueueChapterDownloadsAsync(target, chaps, token).ConfigureAwait(false);

            _logger.LogInformation("Queued re-download of chapter {Chapter} for series {Series} from {Provider}",
                chapterNumber, series.Title, target.Provider);
            return new RedownloadResult(RedownloadOutcome.Queued, target.Provider, chaps.Count);
        }
       // Private helper methods
        private async Task<Models.Database.SeriesEntity?> FindExistingSeriesAsync(AugmentedResponseDto ProviderSeriesDetails,
            SettingsDto settings, Dictionary<string, Guid> paths, Guid ownerId, string? ownerUsername, CancellationToken token)
        {
            if (ProviderSeriesDetails.StorageFolderPath.StartsWith(settings.StorageFolder))
                ProviderSeriesDetails.StorageFolderPath = ProviderSeriesDetails.StorageFolderPath[settings.StorageFolder.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Per-user library separation: every series' files live under
            // {StorageFolder}/{ownerUsername}/... — prepend the owner's subdirectory
            // unless it's already there (re-adding to an existing owned series
            // already carries its real, prefixed StoragePath here).
            if (!string.IsNullOrEmpty(ownerUsername) &&
                !ProviderSeriesDetails.StorageFolderPath.StartsWith(ownerUsername + "/", StringComparison.OrdinalIgnoreCase) &&
                !ProviderSeriesDetails.StorageFolderPath.Equals(ownerUsername, StringComparison.OrdinalIgnoreCase))
            {
                ProviderSeriesDetails.StorageFolderPath = $"{ownerUsername}/{ProviderSeriesDetails.StorageFolderPath}";
            }

            ProviderSeriesDetails.StorageFolderPath = settings.StorageFolder.GetActualDirectoryPathCaseInsensitive(
                ProviderSeriesDetails.StorageFolderPath);

            if (paths.TryGetValue(ProviderSeriesDetails.StorageFolderPath, out Guid id))
            {
                Models.Database.SeriesEntity? byPath = await _db.Series.FirstOrDefaultAsync(s => s.Id == id, token).ConfigureAwait(false);
                if (byPath != null && SeriesQueryService.CanAccessSeries(byPath.OwnerId, ownerId, false))
                    return byPath;
            }

            // Search by title similarity — scoped to the requesting owner's own
            // library (or unowned legacy rows), so two different users adding "the
            // same" manga get independent series instead of one clobbering the
            // other's provider data.
            var allProvs = await (
                from sp in _db.SeriesProviders
                join se in _db.Series on sp.SeriesId equals se.Id
                where se.OwnerId == ownerId || se.OwnerId == Guid.Empty
                select new { sp.Title, sp.SeriesId }
            ).ToListAsync(token).ConfigureAwait(false);

            foreach (var n in allProvs)
            {
                foreach (var ser in ProviderSeriesDetails.Series)
                {
                    if (n.Title.AreStringSimilar(ser.Title, 0))
                    {
                        return await _db.Series.FirstOrDefaultAsync(a => a.Id == n.SeriesId, token)
                            .ConfigureAwait(false);
                    }
                }
            }

            return null;
        }

        private async Task<List<SeriesProviderEntity>> ProcessSeriesProvidersAsync(AugmentedResponseDto ProviderSeriesDetails, List<SeriesProviderEntity> existingProviders, CancellationToken token = default)
        {
            List<ImportProviderSnapshot> pInfos = ProviderSeriesDetails.LocalInfo?.Providers ?? [];

            foreach (var fs in ProviderSeriesDetails.Series)
            {
                ImportProviderSnapshot? pInfo = FindMatchingImportProviderSnapshot(pInfos, fs);
                if (pInfo != null)
                    pInfos.Remove(pInfo);

                var existingProvider = existingProviders.FirstOrDefault(sp => sp.IsMatchingProvider(fs));
                if (existingProvider != null)
                {
                    string provider = fs.Provider;
                    if (!string.IsNullOrEmpty(fs.Scanlator))
                        provider += "-" + fs.Scanlator;
                    
                    _logger.LogInformation("Found existing Provider for '{Title}': {Lang}/{provider}.",
                        fs.Title, fs.Lang, provider);
                    
                    await InternalCreateOrUpdateProviderFromProviderSeriesDetailsAsync(fs, existingProvider, token).ConfigureAwait(false);
                }
                else
                {
                    existingProvider = await InternalCreateOrUpdateProviderFromProviderSeriesDetailsAsync(fs,null, token).ConfigureAwait(false);
                    _db.SeriesProviders.Add(existingProvider);
                    existingProviders.Add(existingProvider);
                }

                if (pInfo != null)
                {
                    InternalAssignArchives(existingProvider, pInfo.Archives);
                    _db.Touch(existingProvider, a => a.Chapters);
                }
            }

            // Add remaining provider infos — check for existing providers first to ensure idempotency
            foreach (ImportProviderSnapshot p in pInfos)
            {
                // Try to find an existing provider by matching provider name + language + scanlator
                var existingProvider = existingProviders.FirstOrDefault(sp =>
                    sp.Provider.Equals(p.Provider, StringComparison.InvariantCultureIgnoreCase) &&
                    sp.Language.Equals(p.Language, StringComparison.InvariantCultureIgnoreCase) &&
                    (string.IsNullOrEmpty(p.Scanlator) ||
                     sp.Scanlator.Equals(p.Scanlator, StringComparison.InvariantCultureIgnoreCase)));

                if (existingProvider != null)
                {
                    // Provider already exists — just update chapters
                    _logger.LogInformation("Found existing provider '{Provider}' for remaining provider info. Updating chapters.",
                        p.Provider);
                    InternalAssignArchives(existingProvider, p.Archives);
                }
                else
                {
                    var nProvider = p.ToSeriesProvider();
                    InternalAssignArchives(nProvider, p.Archives);
                    _db.SeriesProviders.Add(nProvider);
                    existingProviders.Add(nProvider);
                }
            }

            return existingProviders;
        }

        private static ImportProviderSnapshot? FindMatchingImportProviderSnapshot(List<ImportProviderSnapshot> pInfos, ProviderSeriesDetails fs)
        {
            foreach (ImportProviderSnapshot p in pInfos)
            {
                if (string.IsNullOrEmpty(p.Scanlator))
                {
                    if (fs.Provider.Equals(p.Provider, StringComparison.InvariantCultureIgnoreCase) &&
                        fs.Lang.Equals(p.Language, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return p;
                    }
                }
                else
                {
                    if (fs.Provider.Equals(p.Provider, StringComparison.InvariantCultureIgnoreCase) &&
                        (fs.Scanlator.Equals(p.Scanlator, StringComparison.InvariantCultureIgnoreCase) ||
                         fs.Scanlator.Equals(p.Provider, StringComparison.InvariantCultureIgnoreCase)) &&
                        fs.Lang.Equals(p.Language, StringComparison.InvariantCultureIgnoreCase))
                    {
                        return p;
                    }
                }
            }
            return null;
        }

        private static void UpdateProviderSettings(SeriesExtendedDto series, Models.Database.SeriesEntity dbSeries)
        {
            foreach (ProviderExtendedDto p in series.Providers)
            {
                SeriesProviderEntity? n = dbSeries.Sources.FirstOrDefault(a => a.Id == p.Id);
                if (n == null)
                    continue;
                
                n.IsDisabled = p.IsDisabled;
                n.IsStorage = p.IsStorage;
                n.IsTitle = p.UseTitle;
                n.IsCover = p.UseCover;
                n.IsStatus = p.UseStatus;
                n.IsLocal = p.IsLocal;
                n.Priority = p.Priority;
                n.ContinueAfterChapter = p.ContinueAfterChapter;
            }

            // The status source is single-select and must resolve to exactly one
            // provider. If the incoming set designates one, clear it from the rest;
            // if it designates none, fall back to the permanent (storage) source so
            // the series status always has a definite authority.
            var sources = dbSeries.Sources.Where(a => !a.IsUninstalled).ToList();
            SeriesProviderEntity? chosenStatus = sources.FirstOrDefault(a => a.IsStatus);
            if (chosenStatus == null)
                chosenStatus = sources.FirstOrDefault(a => a.IsStorage) ?? sources.FirstOrDefault(a => a.IsCover) ?? sources.FirstOrDefault();
            foreach (SeriesProviderEntity a in sources)
                a.IsStatus = ReferenceEquals(a, chosenStatus);
        }

        private void InternalAssignArchives(SeriesProviderEntity provider, List<ProviderArchiveSnapshot>? archives)
        {
            provider.AssignArchives(archives);
            _db.Touch(provider, e => e.Chapters);
        }

        private async Task<SeriesProviderEntity> InternalCreateOrUpdateProviderFromProviderSeriesDetailsAsync(ProviderSeriesDetails fs, SeriesProviderEntity? provider = null, CancellationToken token = default)
        {
            provider = await fs.CreateOrUpdateAsync(_cache, provider, token).ConfigureAwait(false);
            _db.Touch(provider, e => e.Chapters);
            return provider;
        }

        /// <summary>
        /// Syncs ExternalMappings from an ImportSeriesSnapshot (e.g., from renzo.json)
        /// into the SeriesMappings table. Used by both:
        /// - Setup Wizard (with UserLevel.Owner)
        /// - Import Series Wizard (with the logged-in user's level)
        /// </summary>
        /// <param name="seriesId">The ID of the series to upsert mappings for.</param>
        /// <param name="localInfo">The snapshot containing ExternalMappings.</param>
        /// <param name="userId">The user ID to associate with the mappings (Guid.Empty for setup wizard).</param>
        /// <param name="userLevel">The user level for role-based overwrite protection.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task SyncExternalMappingsFromSnapshotAsync(
            Guid seriesId,
            ImportSeriesSnapshot localInfo,
            Guid userId,
            UserLevel userLevel,
            CancellationToken token = default)
        {
            var mappings = localInfo?.Series.ExternalMappings;
            if (mappings == null || mappings.Count == 0)
                return;

            foreach (var mapping in mappings)
            {
                if (string.IsNullOrEmpty(mapping.Provider) || string.IsNullOrEmpty(mapping.ExternalId))
                    continue;

                if (!Enum.TryParse<ScrobblerProvider>(mapping.Provider, out var provider))
                {
                    _logger.LogWarning("Unknown scrobbler provider '{Provider}' in ExternalMappings for series {SeriesId}",
                        mapping.Provider, seriesId);
                    continue;
                }

                var existing = await _db.SeriesMappings
                    .FirstOrDefaultAsync(m => m.SeriesId == seriesId && m.Provider == provider, token)
                    .ConfigureAwait(false);

                if (existing != null)
                {
                    // Only overwrite if the new user's level >= existing user's level
                    if (userLevel >= existing.UserRole)
                    {
                        existing.ExternalSeriesId = mapping.ExternalId;
                        existing.ExternalSeriesTitle = mapping.ExternalTitle;
                        existing.UserUid = userId;
                        existing.UserRole = userLevel;
                        existing.UpdateDate = DateTime.UtcNow;
                    }
                }
                else
                {
                    _db.SeriesMappings.Add(new SeriesMappingEntity
                    {
                        Id = Guid.NewGuid(),
                        SeriesId = seriesId,
                        Provider = provider,
                        ExternalSeriesId = mapping.ExternalId,
                        ExternalSeriesTitle = mapping.ExternalTitle,
                        UserUid = userId,
                        UserRole = userLevel,
                        UpdateDate = DateTime.UtcNow
                    });
                }
            }

            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            _logger.LogDebug("Synced {Count} ExternalMappings to SeriesMappings for series {SeriesId}",
                mappings.Count, seriesId);
        }

        private async Task<Models.Database.SeriesEntity> ConsolidateDBSeriesFromProvidersAsync(Models.Database.SeriesEntity? dbSeries,
            List<SeriesProviderEntity> providers, string path, bool startDisabled, decimal? startFromChapter, CancellationToken token = default)
        {
            var consolidatedSeries = providers.ToProviderSeriesDetails();
            
            if (dbSeries != null)
            {
                dbSeries.FillSeriesFromProviderSeriesDetails(consolidatedSeries, startFromChapter);
            }
            else
            {
                dbSeries = consolidatedSeries.ToSeries(path);
                dbSeries.PauseDownloads = startDisabled;
                dbSeries.StartFromChapter = startFromChapter;
                await _db.Series.AddAsync(dbSeries, token).ConfigureAwait(false);
            }

            return dbSeries;
        }

        /// <summary>
        /// Updates all SeriesMappings that were created with UserUid == Guid.Empty (setup wizard)
        /// to the actual owner user ID. Called after the owner is chosen/created in the setup wizard.
        /// </summary>
        /// <param name="ownerId">The actual owner user ID.</param>
        /// <param name="token">Cancellation token.</param>
        public async Task UpdateSeriesMappingsOwnerAsync(Guid ownerId, CancellationToken token = default)
        {
            var orphanMappings = await _db.SeriesMappings
                .Where(m => m.UserUid == Guid.Empty)
                .ToListAsync(token)
                .ConfigureAwait(false);

            if (orphanMappings.Count == 0)
                return;

            foreach (var mapping in orphanMappings)
            {
                mapping.UserUid = ownerId;
            }

            await _db.SaveChangesAsync(token).ConfigureAwait(false);
            _logger.LogDebug("Updated {Count} SeriesMappings UserUid from Guid.Empty to owner {OwnerId}",
                orphanMappings.Count, ownerId);
        }

        private class ComboSeries
        {
            public string MihonId { get; set; }
            public ParsedManga? Series { get; set; }
            public List<ParsedChapter> Chapters { get; set; } = [];
        }

    }

    /// <summary>Outcome of a single-chapter (re-)download request.</summary>
    public enum RedownloadOutcome
    {
        Queued,
        Paused,
        SeriesNotFound,
        NoSourceAvailable,
        ChapterNotFound
    }

    /// <summary>Result of <see cref="SeriesCommandService.RedownloadChapterAsync"/>.</summary>
    public class RedownloadResult
    {
        public RedownloadResult(RedownloadOutcome outcome, string? sourceProviderName = null, int queued = 0)
        {
            Outcome = outcome;
            SourceProviderName = sourceProviderName;
            Queued = queued;
        }

        public RedownloadOutcome Outcome { get; }
        public string? SourceProviderName { get; }
        public int Queued { get; }
    }
}

