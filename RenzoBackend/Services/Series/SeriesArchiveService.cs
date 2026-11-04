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
            // The series lock every writer of the chapter lists takes, held for the
            // whole check: it reads the rows, looks at the disk, then writes the
            // rows back whole, and a download finishing in between used to be
            // saved over (its file then missing from the database).
            using var held = await Downloads.DownloadCommandService.SeriesLock.LockAsync(seriesId.ToString(), token).ConfigureAwait(false);
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

                    // The archive is gone. Its download is, not the chapter: on a
                    // source that lists chapters the row stays and the chapter is
                    // wanted again (the reader's missing-file heal does the same).
                    // Removing the row took the chapter off the list whenever no
                    // other source happened to list it. Only a local or Unknown
                    // row, which was nothing but its file, goes.
                    if (!File.Exists(archivePath))
                    {
                        if (provider.IsLocal || provider.IsUnknown)
                            chaptersToRemove.Add(chapter);
                        else
                        {
                            chapter.Filename = null;
                            chapter.DownloadDate = null;
                            if (provider.ContinueAfterChapter == null || provider.ContinueAfterChapter < chapter.Number)
                                chapter.ShouldDownload = true;
                            _db.Touch(provider, c => c.Chapters);
                            dbChanged = true;
                        }
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

                // A local or Unknown source with nothing left goes. Any other
                // source stays however empty it is: a newly added one whose first
                // listing failed was deleted here before it ever got a chance.
                if (provider.Chapters.Count == 0 && (provider.IsLocal || provider.IsUnknown))
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

            int adopted = AdoptOrphanArchives(series, basePath);
            if (adopted > 0)
                dbChanged = true;
            if (ClearStaleDeletedFlags(series) > 0)
                dbChanged = true;

            // Persist all DB changes
            if (dbChanged)
            {
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            // The rows are written: the slow part below (opening every archive)
            // must not hold up downloads of this series finishing.
            held.Dispose();
            if (adopted > 0)
            {
                _logger.LogInformation(
                    "Put {Count} archive(s) of series {Series} back on their chapters: they were in its folder with nothing pointing at them.",
                    adopted, series.Title);
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

        /// <summary>Our own archive names: "[Provider-Scanlator][lang] Title (chapter title) 012".</summary>
        private static readonly System.Text.RegularExpressions.Regex OwnArchiveName = new(
            @"^\[(?<tag>[^\]]+)\](?:\[[^\]]*\])?\s.*\s(?<num>\d+(?:\.\d+)?)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>Reads the source tag and chapter number from one of our archive names; false for anything else.</summary>
        internal static bool TryParseOwnArchiveName(string file, out string tag, out decimal number)
        {
            tag = string.Empty;
            number = 0;
            var m = OwnArchiveName.Match(Path.GetFileNameWithoutExtension(file));
            if (!m.Success || !decimal.TryParse(m.Groups["num"].Value, System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture, out number))
                return false;
            tag = m.Groups["tag"].Value;
            return true;
        }

        /// <summary>The "[...]" tag MakeFileNameSafe gives a source's files.</summary>
        internal static string FileTag(SeriesProviderEntity p)
        {
            string tag = p.Provider.Replace("-", "_");
            if (!string.IsNullOrEmpty(p.Scanlator) && p.Provider != p.Scanlator)
                tag += "-" + p.Scanlator;
            return tag.Replace("[", "(").Replace("]", ")");
        }

        /// <summary>
        /// Takes the deleted flag off rows it no longer means anything on. The chapter
        /// list shows a flagged row's chapter (ModelExtensions.ListedChapterGroups),
        /// but downloads, "Check purchases" and auto-buy skip flagged rows, so such a
        /// chapter was listed and could never be fetched (116 of them on 2026-11-04,
        /// e.g. Mushoku Tensei 110, the Healer's paid 23-58). The flags came from code
        /// that no longer sets them: archive assignment, the bad-archive cleanup, the
        /// disabled-source prune.
        ///
        /// On sources that list chapters (not local or Unknown): a flagged row with no
        /// file is unflagged. A row the post-download dedup flagged (file name "") is a
        /// copy beside the one kept, and stays flagged while some row has the chapter's
        /// file; with no file left anywhere it is unflagged too, so the chapter can be
        /// fetched again. A row whose link another row of the same source has with a
        /// file is a duplicate listing of that chapter and is left alone.
        /// </summary>
        private int ClearStaleDeletedFlags(Models.Database.SeriesEntity series)
        {
            HashSet<decimal> withFile = series.Sources.SelectMany(p => p.Chapters)
                .Where(c => c.Number != null && !string.IsNullOrEmpty(c.Filename))
                .Select(c => c.Number!.Value)
                .ToHashSet();
            int cleared = 0;
            foreach (SeriesProviderEntity p in series.Sources.Where(p => !p.IsLocal && !p.IsUnknown))
            {
                HashSet<string> urlsWithFile = p.Chapters
                    .Where(c => !string.IsNullOrEmpty(c.Filename) && !string.IsNullOrEmpty(c.Url))
                    .Select(c => c.Url!)
                    .ToHashSet(StringComparer.Ordinal);
                bool touched = false;
                foreach (Chapter c in p.Chapters.Where(c => c.IsDeleted && c.Number != null))
                {
                    if (c.Filename == "" && withFile.Contains(c.Number!.Value))
                        continue; // a dedup copy beside the kept one
                    if (c.Filename is { Length: > 0 })
                        continue;
                    if (!string.IsNullOrEmpty(c.Url) && urlsWithFile.Contains(c.Url))
                        continue; // a duplicate listing of a chapter the source has a file for
                    c.IsDeleted = false;
                    c.Filename = null;
                    touched = true;
                    cleared++;
                }
                if (touched)
                    _db.Touch(p, x => x.Chapters);
            }
            return cleared;
        }

        /// <summary>An archive written this recently may be a download being moved into place; it is left alone.</summary>
        private static readonly TimeSpan AdoptAfter = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Downloads fill in the chapter list: an archive in the series folder that
        /// no row points at is put back on the chapter it belongs to, when that is
        /// clearly one chapter of one attached source that lists it and has no file
        /// for it. Such files are rows lost to an old overwrite or a dedup whose
        /// kept copy's row went (12 numbers in 4 series on 2026-11-04 listed only
        /// as deleted rows while their files sat on disk).
        ///
        /// Deliberately not more:
        /// <list type="bullet">
        /// <item>Only files with our own naming, and only when exactly one attached
        /// source's name prefix ("[tag][lang] Title ") matches: two sources can share
        /// a tag, and attaching a file to the wrong one let the post-download dedup
        /// delete it.</item>
        /// <item>Only a chapter that source lists. A file from a source no longer on
        /// the series (MangaK's 1.1, 1.2, 1.3 beside Philia's 1, 2, 3) is another
        /// split of chapters already listed: put on the list it would show each
        /// chapter twice and send Continue back to the start. Those stay with the
        /// import review, where the owner matches them.</item>
        /// <item>Never a chapter already downloaded (a second copy), nor a file
        /// written in the last 15 minutes (a download being moved in).</item>
        /// </list>
        /// Returns how many were attached; the caller holds the series lock and saves.
        /// </summary>
        internal int AdoptOrphanArchives(Models.Database.SeriesEntity series, string basePath)
        {
            if (!Directory.Exists(basePath))
                return 0;
            var referenced = new HashSet<string>(
                series.Sources.SelectMany(p => p.Chapters).Where(c => !string.IsNullOrEmpty(c.Filename)).Select(c => c.Filename!),
                StringComparer.OrdinalIgnoreCase);
            HashSet<decimal> downloaded = series.Sources.SelectMany(p => p.Chapters)
                .Where(c => c.Number != null && !string.IsNullOrEmpty(c.Filename))
                .Select(c => c.Number!.Value)
                .ToHashSet();
            var prefixes = series.Sources
                .Where(p => !p.IsUnknown && !p.IsLocal)
                .Select(p => (Source: p, Prefix: ArchiveHelperService.MakeFileNameSafe(p.Provider, p.Scanlator, p.Title, p.Language, null) + " "))
                .ToList();

            int adopted = 0;
            List<string> files;
            try { files = Directory.EnumerateFiles(basePath).Where(ArchiveHelperService.IsArchiveFile).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
            catch (Exception e)
            {
                _logger.LogWarning(e, "Could not list {Path} for unreferenced archives.", basePath);
                return 0;
            }
            DateTime settled = DateTime.UtcNow - AdoptAfter;
            foreach (string full in files)
            {
                string file = Path.GetFileName(full);
                if (referenced.Contains(file) || !TryParseOwnArchiveName(file, out _, out decimal number))
                    continue;
                if (downloaded.Contains(number) || File.GetLastWriteTimeUtc(full) > settled)
                    continue;
                string stem = Path.GetFileNameWithoutExtension(file);
                var owners = prefixes.Where(x => stem.StartsWith(x.Prefix, StringComparison.Ordinal)).Select(x => x.Source).Distinct().ToList();
                if (owners.Count != 1)
                    continue;
                Chapter? row = owners[0].Chapters.FirstOrDefault(c => c.Number == number && string.IsNullOrEmpty(c.Filename));
                if (row == null)
                    continue;
                row.Filename = file;
                row.DownloadDate = File.GetLastWriteTimeUtc(full);
                row.ShouldDownload = false;
                row.IsDeleted = false;
                row.DownloadDeleted = false;
                _db.Touch(owners[0], p => p.Chapters);
                downloaded.Add(number);
                adopted++;
            }
            return adopted;
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

                        // The bad file goes and the chapter is wanted again; it
                        // stays a chapter the source lists (no deleted flag, which
                        // only ever kept it from being downloaded again).
                        ch.Filename = null;
                        ch.DownloadDate = null;
                        ch.ShouldDownload = true;
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