using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Jobs;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Jobs.Report;
using RenzoBackend.Services.Opds;
using RenzoBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;

namespace RenzoBackend.Services.Series
{
    /// <summary>
    /// Service responsible for archive operations and series integrity checks
    /// </summary>
    public class SeriesArchiveService
    {
        private readonly AppDbContext _db;
        private readonly SettingsService _settings;
        private readonly ArchiveHelperService _archiveHelper;
        private readonly JobHubReportService _reportingService;
        private readonly ILogger<SeriesArchiveService> _logger;
        private readonly SeriesStateService _stateService;
        private readonly HashCacheService _hashCache;

        public SeriesArchiveService(AppDbContext db, 
            SettingsService settings, ArchiveHelperService archiveHelper,
            JobHubReportService reportingService, ILogger<SeriesArchiveService> logger,
            SeriesStateService stateService,
            HashCacheService hashCache)
        {
            _db = db;
            _settings = settings;
            _archiveHelper = archiveHelper;
            _reportingService = reportingService;
            _logger = logger;
            _stateService = stateService;
            _hashCache = hashCache;
        }

        /// <summary>
        /// Verifies the integrity of series archive files
        /// </summary>
        /// <param name="seriesId">The series ID to verify</param>
        /// <param name="force">If true, re-populate pages even if already present</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Series integrity result</returns>
        public async Task<SeriesIntegrityResultDto> VerifyIntegrityAsync(Guid seriesId, bool force = false, CancellationToken token = default)
        {
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            Models.Database.SeriesEntity? series = await _db.Series.Include(a => a.Sources).Where(a => a.Id == seriesId)
                .FirstOrDefaultAsync(token).ConfigureAwait(false);
            
            if (series == null)
                throw new ArgumentException("Invalid series Id");
            
            string basePath = Path.Combine(settings.StorageFolder, series.StoragePath);
            bool dbChanged = false;

            // SAFETY GUARD (mergerfs / network-backed /series): never delete a
            // chapter's download record when the storage mount looks unavailable.
            // If the mount isn't ready at container startup — a real race on
            // mergerfs — every File.Exists() below returns false, which would wipe
            // the whole library's downloaded state and re-queue everything. When
            // the series folder is missing, or it exists but NONE of its expected
            // archives are present, treat storage as not-ready and skip removal.
            List<Chapter> downloadedChapters = series.Sources
                .SelectMany(p => p.Chapters)
                .Where(c => !string.IsNullOrEmpty(c.Filename))
                .ToList();
            if (downloadedChapters.Count > 0)
            {
                bool folderMissing = !Directory.Exists(basePath);
                bool nonePresent = !folderMissing &&
                    !downloadedChapters.Any(c => File.Exists(Path.Combine(basePath, c.Filename!)));
                if (folderMissing || nonePresent)
                {
                    _logger.LogWarning(
                        "Skipping integrity verify for series {SeriesId}: storage at '{Path}' is unavailable ({Reason}) — the /series mount may not be ready. No chapters removed.",
                        seriesId, basePath, folderMissing ? "folder missing" : "no archives found");
                    return new SeriesIntegrityResultDto { Success = true, BadFiles = [] };
                }
            }

            // Process each provider
            var providersToRemove = new List<SeriesProviderEntity>();
            int restored = 0;

            foreach (SeriesProviderEntity provider in series.Sources)
            {
                var chaptersToRemove = new List<Chapter>();

                foreach (Chapter chapter in provider.Chapters.Where(c => !string.IsNullOrEmpty(c.Filename)))
                {
                    string archivePath = Path.Combine(basePath, chapter.Filename);

                    // Remove chapter if the archive file does not exist on disk
                    if (!File.Exists(archivePath))
                    {
                        chaptersToRemove.Add(chapter);
                        continue;
                    }

                    // The archive is right there on disk, so this chapter is not
                    // gone — whatever marked it deleted was wrong. A folder scan
                    // that came back empty or partial (a storage hiccup, a moved
                    // library folder) marks every unmatched chapter deleted, and
                    // deleted chapters are hidden from the chapter list, so a
                    // series can silently lose its whole back catalogue while the
                    // files sit untouched. Restore it.
                    if (chapter.IsDeleted)
                    {
                        chapter.IsDeleted = false;
                        restored++;
                        _db.Touch(provider, c => c.Chapters);
                        dbChanged = true;
                    }

                    // Populate pages if empty or force is true
                    if (chapter.Pages.Count == 0 || force)
                    {
                        var images = ArchiveHelperService.GetImageFiles(archivePath);
                        chapter.Pages = images;
                        chapter.PageCount = images.Count;
                        _db.Touch(provider, c => c.Chapters);
                        dbChanged = true;
                    }
                }

                // Remove collected chapters from the provider
                foreach (Chapter ch in chaptersToRemove)
                {
                    provider.Chapters.Remove(ch);
                    dbChanged = true;
                }

                if (chaptersToRemove.Count > 0)
                {
                    _db.Touch(provider, c => c.Chapters);
                }

                // If provider has no chapters left, mark for removal
                if (provider.Chapters.Count == 0)
                {
                    providersToRemove.Add(provider);
                }
            }

            // Remove empty providers
            foreach (SeriesProviderEntity sp in providersToRemove)
            {
                _db.SeriesProviders.Remove(sp);
                series.Sources.Remove(sp);
                dbChanged = true;
            }

            // Persist all DB changes
            if (dbChanged)
            {
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }

            if (restored > 0)
            {
                _logger.LogInformation(
                    "Restored {Count} chapter(s) of series {Series} that were flagged deleted while their archives were present on disk.",
                    restored, series.Title);
            }

            // Clean up hash cache entries for removed chapters
            try
            {
                // This method also handles hash cleanup internally via its loops
                await _stateService.SyncToRenzoJsonAsync(series.Id, token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to sync renzo.json after integrity verify for series {SeriesId}", series.Id);
            }

            // Return integrity result for remaining valid chapters
            List<Chapter> validChapters = series.Sources.SelectMany(a => a.Chapters)
                .Where(a => !string.IsNullOrEmpty(a.Filename)).ToList();

            return GetIntegrityResult(basePath, validChapters);
        }

        /// <summary>
        /// Cleans up corrupted series files and marks chapters for re-download
        /// </summary>
        /// <param name="seriesId">The series ID to cleanup</param>
        /// <param name="token">Cancellation token</param>
        public async Task CleanupSeriesAsync(Guid seriesId, CancellationToken token = default)
        {
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            Models.Database.SeriesEntity? series = await _db.Series.Include(a => a.Sources).Where(a => a.Id == seriesId)
                .FirstOrDefaultAsync(token).ConfigureAwait(false);
            
            if (series == null)
                throw new ArgumentException("Invalid series Id");
            
            List<Chapter> chaps = series.Sources.SelectMany(a => a.Chapters)
                .Where(a => !string.IsNullOrEmpty(a.Filename)).ToList();
            string basePath = Path.Combine(settings.StorageFolder, series.StoragePath);
            SeriesIntegrityResultDto sr = GetIntegrityResult(basePath, chaps);
            bool update = false;

            foreach (ArchiveIntegrityResultDto r in sr.BadFiles)
            {
                if (r.Result == ArchiveResult.NoImages || r.Result == ArchiveResult.NotAnArchive)
                {
                    string finalName = Path.Combine(basePath, r.Filename);
                    try
                    {
                        File.Delete(finalName);
                    }
                    catch (Exception)
                    {
                        _logger.LogWarning("Unable to delete file {finalName}", finalName);
                    }
                }
                Chapter? chapter = chaps.FirstOrDefault(a => a.Filename == r.Filename);

                
                foreach (SeriesProviderEntity s in series.Sources)
                {
                    foreach (Chapter ch in s.Chapters.Where(a => a.Filename == r.Filename))
                    {
                        // Clean up hash cache before removing the filename reference
                        if (!string.IsNullOrEmpty(ch.Filename))
                        {
                            _hashCache.DeleteChapterHash(series.StoragePath, ch.Filename);
                        }

                        ch.Filename = null;
                        ch.IsDeleted = true;
                        _db.Touch(s, c => c.Chapters);
                        update = true;
                        if (s.ContinueAfterChapter >= ch.Number)
                            s.ContinueAfterChapter = ch.Number - 1;
                    }
                }
            }

            if (update)
                await _db.SaveChangesAsync(token).ConfigureAwait(false);

            // Sync renzo.json after cleanup - always sync even if no changes detected
            // since file deletions may have occurred
            await _stateService.SyncToRenzoJsonAsync(series.Id, token).ConfigureAwait(false);
        }

        /// <summary>
        /// Updates all series titles and comic info files
        /// </summary>
        /// <param name="jobInfo">Job information for progress reporting</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Job result</returns>
        public async Task<JobResult> UpdateAllSeriesAsync(JobInfo jobInfo, CancellationToken token = default)
        {
            ProgressReporter progress = _reportingService.CreateReporter(jobInfo);
            await _archiveHelper.UpdateAllTitlesAndAddComicInfoAsync(progress, false, token).ConfigureAwait(false);
            return JobResult.Success;
        }

        /// <summary>
        /// Verifies the integrity of ALL series in the library.
        /// Iterates each series and runs VerifyIntegrityAsync on it.
        /// </summary>
        /// <param name="jobInfo">Job information for progress reporting</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Job result</returns>
        public async Task<JobResult> VerifyAllSeriesAsync(JobInfo jobInfo, CancellationToken token = default)
        {
            // Top-level storage-readiness guard: if the whole /series mount is
            // missing or empty (e.g. mergerfs not mounted yet at container start),
            // do NOT run the destructive per-series verify — it would treat every
            // downloaded chapter as missing and wipe the library's download state.
            SettingsDto settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            bool storageReady = !string.IsNullOrEmpty(settings.StorageFolder)
                && Directory.Exists(settings.StorageFolder)
                && Directory.EnumerateFileSystemEntries(settings.StorageFolder).Any();
            if (!storageReady)
            {
                _logger.LogWarning(
                    "Skipping full integrity verification: storage folder '{Folder}' is missing or empty — the /series mount may not be ready yet. No chapters removed.",
                    settings.StorageFolder);
                return JobResult.Success;
            }

            var seriesIds = await _db.Series
                .Select(s => s.Id)
                .ToListAsync(token)
                .ConfigureAwait(false);

            _logger.LogInformation("Starting full series integrity verification across {Count} series. This may take a while depending on library size and archive file sizes.", seriesIds.Count);

            int totalSeries = seriesIds.Count;
            int totalBadFiles = 0;
            int affectedSeries = 0;
            int processed = 0;

            foreach (var seriesId in seriesIds)
            {
                if (token.IsCancellationRequested)
                    break;

                processed++;
                try
                {
                    var result = await VerifyIntegrityAsync(seriesId, false, token).ConfigureAwait(false);
                    if (result.BadFiles.Count > 0)
                    {
                        affectedSeries++;
                        totalBadFiles += result.BadFiles.Count;
                        _logger.LogWarning("Series {SeriesId} has {BadCount} bad file(s)", seriesId, result.BadFiles.Count);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to verify integrity for series {SeriesId}", seriesId);
                }

                // Log progress every 50 series
                if (processed % 50 == 0)
                {
                    _logger.LogInformation("Series verification progress: {Processed}/{Total}", processed, totalSeries);
                }
            }

            if (totalBadFiles > 0)
            {
                _logger.LogWarning("Series verification complete. {Total} series checked, {BadCount} bad files found across {AffectedSeries} series.",
                    totalSeries, totalBadFiles, affectedSeries);
            }
            else
            {
                _logger.LogInformation("Series verification complete. All {Total} series passed integrity check.", totalSeries);
            }

            return JobResult.Success;
        }

        /// <summary>
        /// Checks archive integrity and returns result
        /// </summary>
        /// <param name="path">Base path for the series</param>
        /// <param name="chapters">List of chapters to check</param>
        /// <returns>Series integrity result</returns>
        private static SeriesIntegrityResultDto GetIntegrityResult(string path, List<Chapter> chapters)
        {
            SeriesIntegrityResultDto result = new SeriesIntegrityResultDto
            {
                BadFiles = []
            };

            foreach (Chapter c in chapters)
            {
                string fileName = Path.Combine(path, c.Filename!);
                ArchiveResult ar = ArchiveHelperService.CheckArchive(fileName);
                if (ar != ArchiveResult.Fine)
                {
                    result.BadFiles.Add(new ArchiveIntegrityResultDto 
                    { 
                        Filename = c.Filename!,
                        Result = ar 
                    });
                }
            }

            result.Success = result.BadFiles.Count == 0;
            return result;
        }
    }
}