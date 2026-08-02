using System.Text.Json;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Jobs;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Jobs.Report;
using RenzoBackend.Services.Opds;
using RenzoBackend.Services.Search;
using RenzoBackend.Services.Series;
using RenzoBackend.Services.Settings;
using RenzoBackend.Utils;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;
using SharpCompress.Common;
using SharpCompress.Writers;
using SharpCompress.Writers.Zip;
using ExtensionChapter = Mihon.ExtensionsBridge.Models.Extensions.Chapter;

namespace RenzoBackend.Services.Downloads
{
    /// <summary>
    /// Service for download command operations following CQRS pattern
    /// </summary>
    public class DownloadCommandService
    {
        private readonly MihonBridgeService _mihon;
        private readonly AppDbContext _db;
        private readonly SettingsService _settings;
        private readonly JobManagementService _jobManagementService;
        private readonly JobHubReportService _reportingService;
        private readonly CadenceCalculationService _cadenceService;
        private readonly string _tempFolder;
        private readonly ILogger<DownloadCommandService> _logger;
        private readonly Series.SeriesStateService _stateService;
        private readonly HashCacheService _hashCache;
        private readonly ThumbCacheService _thumb;
        private readonly Series.VComicsContentService _vcomics;
        private static readonly KeyedAsyncLock _lock = new KeyedAsyncLock();

        public DownloadCommandService(
            MihonBridgeService mihon,
            AppDbContext db,
            SettingsService settings,
            JobManagementService jobManagementService,
            JobHubReportService reportingService,
            CadenceCalculationService cadenceService,
            IConfiguration config,
            ILogger<DownloadCommandService> logger,
            Series.SeriesStateService stateService,
            HashCacheService hashCache,
            ThumbCacheService thumb,
            Series.VComicsContentService vcomics)
        {
            _vcomics = vcomics;
            _mihon = mihon;
            _db = db;
            _settings = settings;
            _jobManagementService = jobManagementService;
            _reportingService = reportingService;
            _cadenceService = cadenceService;
            _logger = logger;
            _stateService = stateService;
            _hashCache = hashCache;
            _thumb = thumb;
            _tempFolder = Path.Combine(config["runtimeDirectory"] ?? "", "Downloads");
        }

        /// <summary>
        /// Downloads a chapter and saves it as a CBZ file
        /// </summary>
        /// <param name="ch">Chapter download information</param>
        /// <param name="job">Job information for progress reporting</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Job result indicating success or failure</returns>
        public async Task<JobResult> DownloadChapterAsync(ChapterDownload ch, JobInfo job, CancellationToken token = default)
        {
            _logger.LogInformation("Starting download for chapter {ParsedNumber} of series {SeriesTitle} from provider {ProviderName}...", ch.Chapter.ParsedNumber, ch.Title, ch.ProviderName);
            // Resolve the owning user once per job (not per progress tick) so the
            // live SignalR broadcast can be scoped to their library — other users
            // never see this download's progress/thumbnail/title.
            Guid? downloadOwnerId = await _db.Series.Where(s => s.Id == ch.SeriesId).Select(s => (Guid?)s.OwnerId).FirstOrDefaultAsync(token).ConfigureAwait(false);
            ProgressReporter reporter = _reportingService.CreateReporter(job, downloadOwnerId);
            DownloadSummary downloadSummary;

            var appSettings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            ISourceInterop src;
            try
            {
                if (ch.MihonProviderId == null)
                {
                    _logger.LogError("MihonProviderId is null for chapter {ParsedNumber} of series {SeriesTitle}", ch.Chapter.ParsedNumber, ch.Title);
                    return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
                }
                src = await _mihon.SourceFromProviderIdAsync(ch.MihonProviderId, token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Unable to get DownloadChapter from {mihonProviderId}", ch.MihonProviderId);
                return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
            }
            if (src==null)
            {
                _logger.LogError("Source for provider ID {ProviderId} not found when downloading chapter {ParsedNumber} of series {SeriesTitle}",
                    ch.MihonProviderId, ch.Chapter.ParsedNumber, ch.Title);
                return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
            }
            string provider = src.Name + "(" + src.Language + ")";
            try
            {
                // Bounded: an extension call into the sidecar can otherwise block
                // forever (no exception, nothing logged) and permanently leak this
                // download's concurrency slot — see SourceTimeout's doc comment.
                // A paid chapter announces itself by throwing here; keep the
                // exception so it can be told apart from a transient failure.
                Exception? pageFailure = null;
                List<Page>? pages = await _mihon.MihonErrorWrapperAsync(
                                () => SourceTimeout.RunAsync(ct => src.GetPagesAsync(ch.Chapter, ct), token),
                                e => pageFailure = e,
                                "Unable to get Pages from Chapter {ParsedNumber}, Series {Title} from {provider}", ch.Chapter.ParsedNumber, ch.Title, provider).ConfigureAwait(false);
                // A coin-gated chapter the user already bought yields nothing here:
                // on the Astro/"vcomics" platform the paid pages are never in the
                // HTML the extension reads, even for the purchaser. Ask the site's
                // own authenticated content endpoint before giving up. Returns null
                // unless the session really owns it, so nothing unpaid slips through.
                if (pages == null || pages.Count == 0)
                {
                    List<Page>? owned = await _vcomics
                        .TryGetPurchasedPagesAsync(ch.Chapter?.RealUrl ?? ch.Chapter?.Url, token).ConfigureAwait(false);
                    if (owned is { Count: > 0 })
                        pages = owned;
                }

                // Paid/locked chapter: retrying can never succeed — no amount of
                // waiting buys it — and re-queuing every 30 minutes for days
                // fills the log with errors and burns a queue slot per chapter,
                // which reads as "this source is broken". Record the lock so the
                // UI shows it as purchasable and stop.
                if (pages == null && ModelExtensions.IsPurchaseError(pageFailure))
                    return await MarkChapterLockedAsync(ch, provider, token).ConfigureAwait(false);

                if (pages==null)
                    return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
                ch.Pages = pages;
                if (ch.Pages.Count == 0)
                {
                    // Zero pages with no error is the other shape of a paywall
                    // (the source withholds them rather than throwing).
                    if (ModelExtensions.IsLockedChapterName(ch.Chapter?.Name) ||
                        ModelExtensions.IsLockedChapterName(ch.Chapter?.ParsedName))
                        return await MarkChapterLockedAsync(ch, provider, token).ConfigureAwait(false);
                    _logger.LogError("No pages found from source for provider {provider} when downloading chapter {ParsedNumber} of series {SeriesTitle}",
                        provider, ch.Chapter.ParsedNumber, ch.Title);
                    return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
                }
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Error getting pages from source for provider ID {ProviderId} when downloading chapter {ParsedNumber} of series {SeriesTitle}",
                    ch.MihonProviderId, ch.Chapter.ParsedNumber, ch.Title);
                return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
            }
            ch.PageCount = ch.Pages.Count;
            downloadSummary = ch.ToDownloadSummary();
            downloadSummary.PageCount = ch.PageCount;
            // Thumbnail is a raw source URL (e.g. a scanlator CDN link) at this
            // point — the same as everywhere else in the app, proxy it through our
            // cache into a /api/image/{key} path the browser can actually load.
            // Un-proxied source thumbnails routinely fail from the browser (hotlink
            // protection, missing referrer/cookies) — this is what broke the
            // Activity Dock's thumbnail.
            await _thumb.PopulateThumbsAsync(downloadSummary, "/api/image/", token).ConfigureAwait(false);
            string providerName = ch.ProviderName;
            if (ch.Scanlator != null)
                providerName += "-" + ch.Scanlator;

            string chapterName = "";
            chapterName = $"chapter {ch.Chapter.ParsedNumber.FormatDecimal()} ";

            string? rchap = null;
            if (!string.IsNullOrEmpty(ch.ChapterName))
            {
                string cc = ch.ChapterName.Trim().ToLowerInvariant();
                if (!cc.Contains("ch.") && !cc.Contains("chapter"))
                    rchap = ch.ChapterName.Trim();
            }

            decimal? maxChap = null;
            SeriesProviderEntity? p = await _db.SeriesProviders.Where(a => a.Id == ch.SeriesProviderId).AsNoTracking().FirstOrDefaultAsync(token).ConfigureAwait(false);
            if (p != null)
                maxChap = p.Chapters.Max(c => c.Number);

            string zipFile = ArchiveHelperService.MakeFileNameSafe(ch.ProviderName, ch.Scanlator, ch.SeriesTitle, ch.Language, ch.Chapter.ParsedNumber, rchap, maxChap) + ".cbz";
            string message = $"Downloading ({providerName}) {ch.Title} {chapterName}...";
            reporter.Report(ProgressStatus.Started, 0, message, downloadSummary);

            float step = 100 / (float)(ch.PageCount);
            float acum = 0;
            int pagesWritten = 0;
            string tempZipPath = Path.Combine(_tempFolder, zipFile);
            bool breaked = false;

            try
            {
                lock (_lock)
                {
                    if (!Directory.Exists(_tempFolder))
                        Directory.CreateDirectory(_tempFolder);
                }

                if (File.Exists(tempZipPath))
                    File.Delete(tempZipPath);

                using (var zipStream = File.OpenWrite(tempZipPath))
                {
                    await using (var zipWriter = await WriterFactory.OpenAsyncWriter(zipStream, ArchiveType.Zip, new ZipWriterOptions(CompressionType.None)).ConfigureAwait(false))
                    {
                        // Pages are fetched with a bounded look-ahead window instead of one
                        // at a time: a chapter is dozens-to-hundreds of independent HTTP
                        // GETs, and doing them serially made a big chapter take minutes of
                        // almost pure round-trip latency. The window caps how many requests
                        // are in flight (and therefore peak memory ≈ window × page size),
                        // while pages are still WRITTEN in order, so the archive is
                        // byte-for-byte what the serial path produced.
                        int window = Math.Clamp(appSettings.PagesInParallelPerChapter, 1, 16);
                        var inFlight = new Queue<(Page page, Task<ContentTypeStream?> task)>();
                        int nextToFetch = 0;

                        // Global memory ceiling shared by every concurrent download: the
                        // per-chapter window alone doesn't bound total memory, since many
                        // chapters run at once. A slot is taken BEFORE a fetch starts and
                        // returned once that page has been written and disposed, so held
                        // bytes can't exceed the configured budget. Snapshot the semaphore
                        // so a settings change mid-chapter can't unbalance our releases.
                        SemaphoreSlim memorySlots = PageMemoryBudget.Current;

                        // Bounded for the same reason as GetPagesAsync above — this is
                        // the call the 2026-07-30 pipeline-wide stall traced back to.
                        Task<ContentTypeStream?> Fetch(Page target) => _mihon.MihonErrorWrapperAsync(
                            () => SourceTimeout.RunAsync(ct => src.GetPageImageAsync(target, ct), token),
                            "Unable to get Page {Page} from Chapter {Chapter}, Series {Title} from {provider}",
                            target.Index + 1, ch.Chapter.ParsedNumber, ch.Title, provider);

                        // Acquire first, then enqueue — if the wait is cancelled nothing is
                        // queued and no slot is held, keeping acquire/release balanced.
                        async Task<bool> QueueNextPageAsync()
                        {
                            if (nextToFetch >= ch.Pages.Count)
                                return false;
                            await memorySlots.WaitAsync(token).ConfigureAwait(false);
                            Page queued = ch.Pages[nextToFetch++];
                            inFlight.Enqueue((queued, Fetch(queued)));
                            return true;
                        }

                        for (int i = 0; i < window; i++)
                        {
                            if (!await QueueNextPageAsync().ConfigureAwait(false))
                                break;
                        }

                        while (inFlight.Count > 0)
                        {
                            (Page pag, Task<ContentTypeStream?> task) = inFlight.Dequeue();
                            bool slotReturned = false;
                            try
                            {
                                int pageIndex = pag.Index;
                                ContentTypeStream? image = await task.ConfigureAwait(false);
                                if (image == null)
                                {
                                    memorySlots.Release();
                                    slotReturned = true;
                                    breaked = true;
                                    break;
                                }

                                using (image)
                                {
                                    (_, string? ext) = image.GetImageMimeTypeAndExtension();
                                    if (ext == null)
                                    {
                                        _logger.LogWarning("Page {Page} of chapter {ChapterNumber} of series {SeriesTitle} is not a valid image", pageIndex + 1, ch.Chapter.ParsedNumber, ch.Title);
                                        ext = ".unk";
                                    }
                                    string fileName = ArchiveHelperService.MakeFileNameSafe(ch.ProviderName, ch.Scanlator, ch.SeriesTitle, ch.Language,
                                                ch.Chapter.ParsedNumber, ch.ChapterName, maxChap, pageIndex + 1, ch.PageCount) + ext;
                                    await zipWriter.WriteAsync(fileName, image).ConfigureAwait(false);
                                }
                                // The image is written and disposed — its memory is free.
                                memorySlots.Release();
                                slotReturned = true;

                                pagesWritten++;
                                acum += step;
                                message = $"Downloading ({providerName}) {ch.Title} {chapterName} {pageIndex}";
                                reporter.Report(ProgressStatus.InProgress, (int)acum, message, downloadSummary);

                                // Keep the window full as each page is consumed.
                                await QueueNextPageAsync().ConfigureAwait(false);
                            }
                            catch (Exception)
                            {
                                if (!slotReturned)
                                    memorySlots.Release();
                                _logger.LogError("Failed to download page {Page} for chapter {ChapterNumber} of series {SeriesTitle}",
                                    pag.Index + 1, ch.Chapter.ParsedNumber, ch.Title);
                                breaked = true;
                                break;
                            }
                        }

                        // A failure abandons the chapter, so drain whatever is still in
                        // flight: dispose the streams and hand back their memory slots,
                        // otherwise both leak.
                        if (inFlight.Count > 0)
                        {
                            foreach ((_, Task<ContentTypeStream?> pending) in inFlight)
                            {
                                try { (await pending.ConfigureAwait(false))?.Dispose(); }
                                catch { /* already failing; nothing to salvage */ }
                                finally { memorySlots.Release(); }
                            }
                            inFlight.Clear();
                        }

                        if (pagesWritten == 0)
                        {
                            breaked = true;
                        }

                        if (!breaked)
                        {
                            using (Stream comicInfo = ArchiveHelperService.CreateComicInfo(ch, pagesWritten).ToStream())
                            {
                                ((ZipWriter)zipWriter).Write("ComicInfo.xml", comicInfo, new ZipWriterEntryOptions { CompressionType = CompressionType.Deflate, ModificationDateTime = DateTime.Now });
                            }
                        }
                    }
                }

                if (breaked)
                {
                    try
                    {
                        File.Delete(tempZipPath);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Failed to delete temporary zip file {TempZipPath}", tempZipPath);
                    }
                    reporter.Report(ProgressStatus.Failed, (int)acum, message, downloadSummary);
                    return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
                }

                string dirPath = Path.Combine(appSettings.StorageFolder, ch.StoragePath);
                if (!Directory.Exists(dirPath))
                    Directory.CreateDirectory(dirPath);

                string finalPath = Path.Combine(dirPath, zipFile);
                try
                {
                    await Task.Run(() => File.Move(tempZipPath, finalPath, true), token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed to move downloaded file from {TempZipPath} to {FinalPath}", tempZipPath, finalPath);
                    reporter.Report(ProgressStatus.Failed, (int)acum, message, downloadSummary);
                    return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
                }

                using (var n = await _lock.LockAsync(ch.SeriesId.ToString(), token).ConfigureAwait(false))
                {
                    SeriesProviderEntity? providerr = await _db.SeriesProviders.FirstOrDefaultAsync(a => a.Id == ch.SeriesProviderId, token).ConfigureAwait(false);
                    if (providerr == null)
                    {
                        _logger.LogWarning("Series Provider {ProviderName} no longer exists.", ch.ProviderName);
                        reporter.Report(ProgressStatus.Completed, 100, "", downloadSummary);
                        return JobResult.Failed;
                    }

                    Models.Chapter? cha = providerr.Chapters.FirstOrDefault(c => c.Number == ch.Chapter.ParsedNumber);
                    bool newRow = cha == null;
                    if (cha == null)
                    {
                        cha = new Models.Chapter();
                        providerr.Chapters.Add(cha);
                        providerr.Chapters = providerr.Chapters.OrderBy(c => c.Number).ToList();
                    }

                    cha.PageCount = pagesWritten;
                    cha.IsDeleted = false;
                    cha.Name = ch.Chapter.Name;
                    cha.Number = ch.Chapter.ParsedNumber;
                    cha.DownloadDate = DateTime.UtcNow;
                    cha.ProviderUploadDate = ch.ComicUploadDateUTC;
                    // "Found" time — set once, never bumped (re-downloads must not
                    // resurface a chapter in Updates). A brand-new row here means
                    // the scan just discovered it → found now. A pre-existing row
                    // without a found time (registered at series-add, or an old
                    // library chapter) takes its stable publish date when real, so
                    // bulk back-catalogue downloads don't flood the Updates feed.
                    cha.DateFetched ??= !newRow && RenzoBackend.Extensions.ModelExtensions.HasRealUploadDate(ch.ComicUploadDateUTC)
                        ? ch.ComicUploadDateUTC
                        : DateTime.UtcNow;
                    cha.Filename = zipFile;
                    cha.ShouldDownload = false;
                    // A successful download proves the chapter is actually reachable
                    // (the site login owns it) — clear the stale lock flag so the
                    // reader stops showing the purchase screen for a file it already
                    // has on disk. IsLocked previously only ever got set to true (by
                    // LockedChapterSupplementService) and nothing ever cleared it, so
                    // purchased/downloaded chapters stayed "Locked" forever.
                    cha.IsLocked = false;
                    providerr.ContinueAfterChapter = providerr.Chapters.MaxNull(c => c.Number);
                    providerr.ChapterCount = providerr.Chapters.Count;
                    _db.Touch(providerr, a => a.Chapters);
                    await _db.SaveChangesAsync(token).ConfigureAwait(false);

                    Models.Database.SeriesEntity s = await _db.Series.Include(a => a.Sources).Where(a => a.Id == providerr.SeriesId).FirstAsync(token);
                    if (providerr.IsStorage)
                    {
                        List<Models.Chapter> chapters = s.Sources.Where(a => !a.IsDisabled && !a.IsUninstalled && !a.IsStorage)
                            .SelectMany(a => a.Chapters).Where(c => c.Number == ch.Chapter.ParsedNumber && !string.IsNullOrEmpty(c.Filename)).ToList();
                        if (chapters.Count > 0)
                        {
                            //Delete temporary sources chapters if needed, since we have the storage one
                            foreach (Models.Chapter c in chapters)
                            {
                                // Clean up hash cache before removing the filename
                                if (!string.IsNullOrEmpty(c.Filename))
                                {
                                    _hashCache.DeleteChapterHash(s.StoragePath, c.Filename);
                                }

                                string rfname = Path.Combine(appSettings.StorageFolder, s.StoragePath, c.Filename!);
                                if (File.Exists(rfname))
                                {
                                    try
                                    {
                                        File.Delete(rfname);
                                    }
                                    catch
                                    {
                                        _logger.LogError("Unable to delete file {rfname}", rfname);
                                    }
                                }
                                c.Filename = string.Empty;
                                c.IsDeleted = true;
                            }
                        }
                        await _db.SaveChangesAsync(token).ConfigureAwait(false);
                    }
                    else
                    {
                        // A remote (non-permanent) source finished this chapter. Enforce
                        // "one downloaded file per chapter": if any OTHER source already
                        // holds it, keep the permanent (storage) copy when present — else
                        // keep this fresh one — and delete the rest. Prevents duplicate
                        // downloads from lingering when two sources race the same chapter.
                        var dupes = s.Sources
                            .Where(a => !a.IsUninstalled)
                            .SelectMany(a => a.Chapters.Select(c => (prov: a, chap: c)))
                            .Where(x => x.chap.Number == ch.Chapter.ParsedNumber
                                && !x.chap.IsDeleted && !string.IsNullOrEmpty(x.chap.Filename))
                            .ToList();
                        if (dupes.Count > 1)
                        {
                            // Winner: with priority upgrades enabled for this series' owner
                            // (per-user, see UserPriorityPrefsExtensions), the per-series
                            // source Priority decides (0 = top; ties → storage copy, then
                            // the copy that just finished). Otherwise the long-standing
                            // rule: a storage source's copy if one exists, else the row we
                            // just completed on this provider.
                            bool redownloadEnabled = s.OwnerId != Guid.Empty
                                && (await _db.Users.FirstOrDefaultAsync(u => u.Id == s.OwnerId, token).ConfigureAwait(false))
                                    ?.GetRedownloadFromHigherPrioritySources() == true;
                            (SeriesProviderEntity prov, Models.Chapter chap) keep;
                            if (redownloadEnabled)
                            {
                                keep = dupes
                                    .OrderBy(x => x.prov.Priority)
                                    .ThenByDescending(x => x.prov.IsStorage)
                                    .ThenByDescending(x => x.prov.Id == providerr.Id)
                                    .First();
                            }
                            else
                            {
                                keep = dupes.FirstOrDefault(x => x.prov.IsStorage);
                                if (keep.prov == null)
                                    keep = dupes.First(x => x.prov.Id == providerr.Id);
                            }

                            foreach (var (prov, c) in dupes)
                            {
                                if (ReferenceEquals(c, keep.chap)) continue;
                                _hashCache.DeleteChapterHash(s.StoragePath, c.Filename!);
                                string rfname = Path.Combine(appSettings.StorageFolder, s.StoragePath, c.Filename!);
                                if (File.Exists(rfname))
                                {
                                    try { File.Delete(rfname); }
                                    catch { _logger.LogError("Unable to delete duplicate file {rfname}", rfname); }
                                }
                                c.Filename = string.Empty;
                                c.IsDeleted = true;
                                _db.Touch(prov, a => a.Chapters);
                            }
                            await _db.SaveChangesAsync(token).ConfigureAwait(false);
                            _logger.LogInformation("Deduplicated chapter {Chapter} of {Series}: kept {Keep}, removed {Count} duplicate copy(ies).",
                                ch.Chapter.ParsedNumber, s.Title, keep.prov!.Provider, dupes.Count - 1);
                        }
                    }

                    await _stateService.SyncToRenzoJsonAsync(s.Id, token).ConfigureAwait(false);
                }

                // Recalculate release cadence after successful download
                await _cadenceService.RecalculateCadenceAsync(ch.SeriesId, token).ConfigureAwait(false);

                message = $"Downloading ({providerName}) {ch.Title} {chapterName} completed.";
                reporter.Report(ProgressStatus.Completed, 100, message, downloadSummary);
                _logger.LogInformation("Download Complete for chapter {ChapterNumber} of series {SeriesTitle} from provider {ProviderName}...", ch.Chapter.ParsedNumber, ch.Title, ch.ProviderName);
                return JobResult.Success;
            }
            catch (Exception e)
            {
                if (File.Exists(tempZipPath))
                {
                    try
                    {
                        File.Delete(tempZipPath);
                    }
                    catch
                    {
                    }
                }
                _logger.LogError(e, "Error downloading chapter {ParsedNumber} of series {SeriesTitle}: {Message}", ch.Chapter.ParsedNumber, ch.Title, e.Message);
                reporter.Report(ProgressStatus.Failed, (int)100, "Error downloading chapter", downloadSummary);
                return await RescheduleDownloadAsync(ch, token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Manages error downloads by retrying or deleting them
        /// </summary>
        /// <param name="id">Download ID</param>
        /// <param name="action">Action to take</param>
        /// <param name="token">Cancellation token</param>
        public async Task ManageErrorDownloadAsync(Guid id, ErrorDownloadAction action, CancellationToken token = default)
        {
            EnqueueEntity? d = await _db.Queues.Where(a => a.Id == id && a.JobType == JobType.Download).AsNoTracking().FirstOrDefaultAsync(token).ConfigureAwait(false);
            if (d == null)
                return;

            if (action == ErrorDownloadAction.Retry)
            {
                if (string.IsNullOrEmpty(d.JobParameters))
                    return;
                ChapterDownload? ch = JsonSerializer.Deserialize<ChapterDownload>(d.JobParameters);
                if (ch == null)
                    return;
                ch.Retries = 0;
                await RescheduleDownloadAsync(ch, token);
                return;
            }

            if (action == ErrorDownloadAction.Delete)
            {
                EnqueueEntity delete = await _db.Queues.FirstAsync(a => a.Id == id, token).ConfigureAwait(false);
                _db.Queues.Remove(delete);
                await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Queues chapter downloads for a series provider
        /// </summary>
        /// <param name="serie">Series provider</param>
        /// <param name="chaps">Chapter downloads to queue</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Job result</returns>
        public async Task<JobResult> QueueChapterDownloadsAsync(SeriesProviderEntity serie, List<ChapterDownload> chaps, CancellationToken token = default)
        {
            string scanlator = string.Empty;
            if (!string.IsNullOrEmpty(serie.Scanlator) && serie.Scanlator != serie.Provider)
                scanlator = ":" + serie.Scanlator;

            if (chaps.Count == 0)
                _logger.LogInformation("Provider {Provider}:{Lang}{scanlator} does not have new Chapters for Series '{Title}'.", serie.Provider, serie.Language, scanlator, serie.Title);
            else
            {
                int updateCount = chaps.Count(a => a.IsUpdate);
                int newCount = chaps.Count - updateCount;
                if (updateCount > 0 && newCount > 0)
                {
                    _logger.LogInformation("Provider {Provider}:{Lang}{scanlator} has {newCount} new Chapters and {updateCount} updated Chapters for Series '{Title}'.", serie.Provider, serie.Language, scanlator, newCount, updateCount, serie.Title);
                }
                else if (updateCount > 0)
                {
                    _logger.LogInformation("Provider {Provider}:{Lang}{scanlator} has {updateCount} updated Chapters for Series '{Title}'.", serie.Provider, serie.Language, scanlator, updateCount, serie.Title);
                }
                else
                {
                    _logger.LogInformation("Provider {Provider}:{Lang}{scanlator} has {newCount} new Chapters for Series '{Title}'.", serie.Provider, serie.Language, scanlator, newCount, serie.Title);
                }

                foreach (ChapterDownload ch in chaps.OrderBy(a => a.Index))
                {
                    // Keyed by series+chapter NUMBER, not provider — the app's
                    // invariant is one downloaded file per chapter regardless of
                    // source (see the post-download dedup below), so a second
                    // source racing to queue the same not-yet-downloaded chapter
                    // must collapse onto the same job instead of downloading it
                    // twice. EnqueueJobAsIsAsync no-ops when a job with this key
                    // is already Waiting/Running, so whichever source enqueues
                    // first wins; the loser's queue attempt is silently skipped.
                    string key = $"{ch.SeriesId}|{ch.Chapter.ParsedNumber.FormatDecimal()}";
                    string groupKey = $"{ch.ProviderName}";
                    await _jobManagementService.EnqueueJobAsync(JobType.Download, ch, Priority.Normal, key, groupKey, ch.SeriesId.ToString(), "Downloads", token).ConfigureAwait(false);
                }
            }
            return JobResult.Success;
        }

        /// <summary>
        /// Reschedules a failed download with retry logic
        /// </summary>
        /// <param name="download">Chapter download to reschedule</param>
        /// <param name="token">Cancellation token</param>
        /// <returns>Job result</returns>
        /// <summary>
        /// Flags a chapter as paid/locked and drops its download. Called when the
        /// source says the chapter must be purchased — a permanent condition, so
        /// the job completes rather than retrying until the retry budget runs out.
        /// </summary>
        private async Task<JobResult> MarkChapterLockedAsync(ChapterDownload download, string provider, CancellationToken token = default)
        {
            _logger.LogInformation(
                "Chapter {ChapterNumber} of series {SeriesTitle} is locked on {provider} (requires purchase) — not retrying.",
                download.Chapter.ParsedNumber, download.Title, provider);
            try
            {
                // Chapters live inside each provider's JSON column, so the flag is
                // set on the matching chapter of every provider row for this series
                // — the paywall belongs to the source, not to one queue entry.
                decimal number = download.Chapter.ParsedNumber;
                List<SeriesProviderEntity> providers = await _db.SeriesProviders
                    .Where(p => p.SeriesId == download.SeriesId)
                    .ToListAsync(token).ConfigureAwait(false);
                bool changed = false;
                foreach (SeriesProviderEntity provRow in providers)
                {
                    if (!string.Equals(provRow.Provider, download.ProviderName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    foreach (Models.Chapter c in provRow.Chapters.Where(c => c.Number == number && !c.IsLocked))
                    {
                        c.IsLocked = true;
                        c.ShouldDownload = false;
                        changed = true;
                    }
                    if (changed)
                        _db.Entry(provRow).Property(p => p.Chapters).IsModified = true;
                }
                if (changed)
                    await _db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "Could not flag chapter {ChapterNumber} of series {SeriesId} as locked.",
                    download.Chapter.ParsedNumber, download.SeriesId);
            }
            return JobResult.Success;
        }

        private async Task<JobResult> RescheduleDownloadAsync(ChapterDownload download, CancellationToken token = default)
        {
            SettingsDto appSettings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
            download.Retries++;
            // Same series+chapter-number key as QueueChapterDownloadsAsync — see there.
            string key = $"{download.SeriesId}|{download.Chapter.ParsedNumber.FormatDecimal()}";

            if (download.Retries > appSettings.ChapterDownloadFailRetries)
            {
                _logger.LogWarning("Max retries reached for chapter {ChapterNumber} of series {SeriesTitle} from {ProviderName}. Giving up.", download.Chapter.ChapterNumber, download.Title, download.ProviderName);
                return JobResult.Failed;
            }

            string groupKey = $"{download.ProviderName}";
            DateTime nextTime = DateTime.UtcNow.Add(appSettings.ChapterDownloadFailRetryTime);
            await _jobManagementService.ScheduleJobAsync(JobType.Download, download, nextTime, "Downloads", key, groupKey, download.SeriesId.ToString(), Priority.Normal, download.Retries, token).ConfigureAwait(false);
            return JobResult.Handled;
        }
    }
}
