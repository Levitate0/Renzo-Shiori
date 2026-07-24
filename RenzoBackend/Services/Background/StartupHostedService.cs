using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Migration;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Jobs;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.ReadState;
using RenzoBackend.Services.Settings;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Core.Utilities;
using Mihon.ExtensionsBridge.Models.Abstractions;
using System.ComponentModel;

namespace RenzoBackend.Services.Background
{
    public class StartupHostedService : IHostedService, IDisposable
    {
        private readonly NouisanceFixer20ExtraLarge _fixes;
        private readonly ILogger<StartupHostedService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly List<Task> _workerTasks = new();
        private CancellationTokenSource? _workerCts;
        private bool _disposed = false;

        public StartupHostedService(ILogger<StartupHostedService> logger, 
            IServiceScopeFactory scopeFactory,
            NouisanceFixer20ExtraLarge fixes,
            IConfiguration config)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _fixes = fixes;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                try
                {
                    // Use a timeout for disposal
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    StopAsync(cts.Token).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error during disposal of StartupHostedService");
                }
                _disposed = true;
                GC.SuppressFinalize(this);
            }
        }

        public async Task<bool> CheckStorageStatusAsync(AppDbContext db, SettingsDto settings, IHostApplicationLifetime lifetime, CancellationToken token = default)
        {

            Models.Database.SeriesEntity? series = await db.Series.AsNoTracking().OrderBy(a=>a.Id).FirstOrDefaultAsync(token).ConfigureAwait(false);

            bool hasArchiveFiles = ArchiveHelperService.ContainsArchiveFilesRecursive(settings.StorageFolder);
            if (!hasArchiveFiles && series!=null)
            {
                _logger.LogError("No archive files found in the storage folder. But database has content, shutting down...");
                lifetime.StopApplication();
                return false;
            }
            else if (hasArchiveFiles && series == null)
            {
                //We have archive files, but no series in the database, we start the wizard setup
                settings.IsWizardSetupComplete = false;
                settings.WizardSetupStepCompleted = 0;
            }
            else
            {
                // We have archive files and series in the database, or everything is empty, we can proceed
                settings.IsWizardSetupComplete = true;
                settings.WizardSetupStepCompleted = 0;
            }

            return true;
        }


        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {

                using var scope = _scopeFactory.CreateScope();
                //Initialize Mihon Bridge
                var mihon = scope.ServiceProvider.GetRequiredService<IBridgeManager>();
                await mihon.InitializeAsync(cancellationToken);


              

                //Run migration if needed
                var migration = scope.ServiceProvider.GetRequiredService<MigrationService>();
                await migration.RunAsync(cancellationToken).ConfigureAwait(false);


                // Initialize other services
                var settingsService = scope.ServiceProvider.GetRequiredService<SettingsService>();
                var providerCacheService = scope.ServiceProvider.GetRequiredService<ProviderCacheService>();
                
                // Load settings
                SettingsDto settings = await settingsService.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
                settingsService.SetThreadSettings(settings);
                // Apply the per-host download concurrency now that the bridge (and its
                // shared OkHttp client) is up. Safe no-op if it can't be reached.
                mihon.SetMaxRequestsPerHost(settings.MaxRequestsPerHost);
                await settingsService.SetTimesSettingsAsync(settings, cancellationToken).ConfigureAwait(false);
                AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken).ConfigureAwait(false);
                //await db.Database.ExecuteSqlRawAsync("PRAGMA busy_timeout=5000;", cancellationToken).ConfigureAwait(false);
                await _fixes.FixThumbnailsOfSeriesWithMissingThumbnailsAsync(cancellationToken).ConfigureAwait(false);

                // One-time backfill: per-user library separation. Every pre-existing
                // series (OwnerId still Guid.Empty from the AddSeriesOwnerId migration
                // default) becomes owned by the instance's Owner-level account — the
                // same account whose physical library files were moved into their own
                // "{Username}/" subdirectory under StorageFolder as part of this
                // rollout. Only runs the StoragePath prefix once the physical move is
                // confirmed on disk, so a redeploy without the move first leaves
                // everything working from its original location instead of 404ing.
                try
                {
                    List<Models.Database.SeriesEntity> unowned = await db.Series
                        .Where(s => s.OwnerId == Guid.Empty)
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                    if (unowned.Count > 0)
                    {
                        UserEntity? owner = await db.Users
                            .Where(u => u.Level == UserLevel.Owner)
                            .OrderBy(u => u.CreatedAt)
                            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
                        if (owner != null)
                        {
                            string ownerDir = Path.Combine(settings.StorageFolder, owner.Username);
                            bool movedToOwnerDir = Directory.Exists(ownerDir) && Directory.EnumerateFileSystemEntries(ownerDir).Any();

                            int pathsFixed = 0;
                            foreach (Models.Database.SeriesEntity s in unowned)
                            {
                                s.OwnerId = owner.Id;
                                if (movedToOwnerDir &&
                                    !s.StoragePath.StartsWith(owner.Username + "/", StringComparison.OrdinalIgnoreCase) &&
                                    !s.StoragePath.Equals(owner.Username, StringComparison.OrdinalIgnoreCase))
                                {
                                    string prefixed = $"{owner.Username}/{s.StoragePath}";
                                    // Only rewrite if the archive folder actually exists at the new
                                    // nested location — otherwise leave StoragePath alone (unowned →
                                    // owned still happens) and log it for manual attention.
                                    if (Directory.Exists(Path.Combine(settings.StorageFolder, prefixed)))
                                    {
                                        s.StoragePath = prefixed;
                                        pathsFixed++;
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Series {Title} ({Id}): expected files at {Path} after the library move but they weren't found — StoragePath left unchanged.",
                                            s.Title, s.Id, Path.Combine(settings.StorageFolder, prefixed));
                                    }
                                }
                            }
                            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Per-user library backfill: assigned {Count} series to {Owner}, updated {Fixed} storage path(s) for the moved library layout.",
                                unowned.Count, owner.Username, pathsFixed);
                        }
                        else
                        {
                            _logger.LogWarning("Per-user library backfill: {Count} series have no owner and no Owner-level account exists to assign them to.", unowned.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Per-user library backfill failed");
                }

                // One-time backfill: per-user source visibility. Every EXISTING user
                // (as of this rollout) keeps seeing every currently-installed source,
                // so nobody's Search/Browse suddenly goes empty. Any user created
                // AFTER this point starts with zero enabled sources — they opt in
                // themselves, and installing a source going forward only auto-enables
                // it for whoever performed that install (see ProviderController).
                try
                {
                    bool alreadySeeded = await db.UserProviders.AnyAsync(cancellationToken).ConfigureAwait(false);
                    if (!alreadySeeded)
                    {
                        List<Guid> userIds = await db.Users.Select(u => u.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
                        List<string> providerIds = await db.Providers.Select(p => p.MihonProviderId).Distinct().ToListAsync(cancellationToken).ConfigureAwait(false);
                        if (userIds.Count > 0 && providerIds.Count > 0)
                        {
                            foreach (Guid uid in userIds)
                            {
                                foreach (string pid in providerIds)
                                {
                                    db.UserProviders.Add(new Models.Database.UserProviderEntity { Id = Guid.NewGuid(), UserId = uid, MihonProviderId = pid });
                                }
                            }
                            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                            _logger.LogInformation("Per-user source visibility backfill: enabled {ProviderCount} sources for {UserCount} existing user(s).",
                                providerIds.Count, userIds.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Per-user source visibility backfill failed");
                }

                // One-time (self-limiting) sweep: file every un-categorized series
                // into its category subfolder ({owner}/{Category}/{leaf}). Series
                // added before categorization — or title-only imports whose source
                // shipped no usable Type — otherwise sit loose at the library root.
                // Runs here, before the job workers start below, so no download is
                // writing into a folder while it's being moved. Best-effort category
                // via SeriesTypeClassifier (biased to Manga); the physical move +
                // DB + renzo.json stay in lockstep via SeriesRelocationService.
                // Converges: once nothing is un-categorized this is a single cheap
                // query on subsequent boots.
                try
                {
                    string[] categories = settings.Categories ?? [];
                    if (settings.CategorizedFolders && categories.Length > 0)
                    {
                        var relocator = scope.ServiceProvider.GetRequiredService<Series.SeriesRelocationService>();
                        List<Models.Database.SeriesEntity> allSeries = await db.Series
                            .Include(s => s.Sources)
                            .ToListAsync(cancellationToken).ConfigureAwait(false);

                        int filed = 0, skipped = 0;
                        foreach (Models.Database.SeriesEntity s in allSeries)
                        {
                            // Skip anything already living in a category folder.
                            if (Series.SeriesRelocationService.SplitPath(s.StoragePath, categories).category != null)
                                continue;

                            IEnumerable<string> providerNames = s.Sources
                                .Select(p => p.Provider)
                                .Where(p => !string.IsNullOrWhiteSpace(p))!;
                            string? category = Series.SeriesTypeClassifier.Classify(s.Genre, providerNames, categories);
                            if (string.IsNullOrEmpty(category))
                                continue;

                            var result = await relocator.RelocateToCategoryAsync(s.Id, category, cancellationToken)
                                .ConfigureAwait(false);
                            if (result.Moved) filed++;
                            else skipped++;
                        }
                        if (filed > 0 || skipped > 0)
                            _logger.LogInformation("Category sweep: filed {Filed} series into category folders ({Skipped} left in place / skipped).", filed, skipped);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Category folder sweep failed");
                }

                // Fix: Ensure IsLocal = true for all providers without MihonProviderId that aren't Unknown
                List<SeriesProviderEntity> localProviderFixes = await db.SeriesProviders
                    .Where(a => string.IsNullOrEmpty(a.MihonProviderId) && !a.IsUnknown && !a.IsLocal)
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                if (localProviderFixes.Count > 0)
                {
                    _logger.LogInformation("Fixing {Count} SeriesProviders with missing IsLocal flag", localProviderFixes.Count);
                    foreach (var p in localProviderFixes)
                        p.IsLocal = true;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
                // Seed the per-series status source for existing libraries: the
                // permanent (storage) source becomes the status authority so the
                // series status stops following whichever source last synced. Only
                // series that don't already designate one are touched.
                try
                {
                    List<Guid> seriesWithStatus = await db.SeriesProviders
                        .Where(a => a.IsStatus)
                        .Select(a => a.SeriesId)
                        .Distinct()
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                    var haveStatus = new HashSet<Guid>(seriesWithStatus);

                    List<SeriesProviderEntity> candidates = await db.SeriesProviders
                        .Where(a => !a.IsUninstalled && !a.IsStatus)
                        .ToListAsync(cancellationToken).ConfigureAwait(false);
                    int seeded = 0;
                    foreach (var grp in candidates.GroupBy(a => a.SeriesId))
                    {
                        if (haveStatus.Contains(grp.Key))
                            continue;
                        var list = grp.ToList();
                        SeriesProviderEntity? pick = list.FirstOrDefault(a => a.IsStorage)
                            ?? list.FirstOrDefault(a => a.IsCover)
                            ?? list.FirstOrDefault(a => a.IsTitle)
                            ?? list.FirstOrDefault();
                        if (pick != null) { pick.IsStatus = true; seeded++; }
                    }
                    if (seeded > 0)
                    {
                        _logger.LogInformation("Seeded status source (permanent) for {Count} series", seeded);
                        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to seed per-series status sources");
                }

                scope.ServiceProvider.GetRequiredService<ReadStateService>().PrefetchCache(await db.Series.ToListAsync(cancellationToken).ConfigureAwait(false));

                // Re-inject saved coin-site login cookies into the shared jar so
                // paid-chapter access survives a restart without a re-login.
                try
                {
                    await scope.ServiceProvider.GetRequiredService<SiteAuth.SiteAuthService>()
                        .RestoreAllAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not restore site-login cookies at startup");
                }

                IHostApplicationLifetime lifetime = scope.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
                JobManagementService jobManagement = scope.ServiceProvider.GetRequiredService<JobManagementService>();
                _logger.LogInformation("Checking Storage folder Status...");
                bool save = await CheckStorageStatusAsync(db, settings, lifetime, cancellationToken).ConfigureAwait(false);
                if (save)
                    await settingsService.SaveSettingsAsync(settings, true, cancellationToken).ConfigureAwait(false);
                // Cache providers
                _logger.LogInformation("Syncing Mihon Extensions Preferences.");
                await providerCacheService.RefreshCacheAsync(false, cancellationToken).ConfigureAwait(false);
                var jobs = await jobManagement.GetRecurringJobsByTypeAsync(JobType.DailyUpdate, cancellationToken).ConfigureAwait(false);
                if (jobs.Count == 0)
                {
                    await jobManagement.ScheduleRecurringJobAsync(JobType.DailyUpdate, (string?)null,null, null,false, TimeSpan.FromDays(1),Priority.Normal, cancellationToken).ConfigureAwait(false);
                }
                // Schedule health status check job (runs every hour)
                var statusJobs = await jobManagement.GetRecurringJobsByTypeAsync(JobType.StatusCheck, cancellationToken).ConfigureAwait(false);
                if (statusJobs.Count == 0)
                {
                    await jobManagement.ScheduleRecurringJobAsync(JobType.StatusCheck, (string?)null, null, null, false, TimeSpan.FromHours(1), Priority.Normal, cancellationToken).ConfigureAwait(false);
                }

                // Schedule daily series verification job
                var verifyJobs = await jobManagement.GetRecurringJobsByTypeAsync(JobType.VerifyAllSeries, cancellationToken).ConfigureAwait(false);
                if (verifyJobs.Count == 0)
                {
                    _logger.LogInformation("Scheduling daily series verification job...");
                    await jobManagement.ScheduleRecurringJobAsync(JobType.VerifyAllSeries, (string?)null, null, null, false, TimeSpan.FromDays(1), Priority.Low, cancellationToken).ConfigureAwait(false);
                }

                // Schedule the rolling library scan (new-chapter check across the
                // whole library) at the user-set interval, clamped 3-12h.
                var scanJobs = await jobManagement.GetRecurringJobsByTypeAsync(JobType.LibraryScan, cancellationToken).ConfigureAwait(false);
                if (scanJobs.Count == 0)
                {
                    TimeSpan scanEvery = TimeSpan.FromHours(Math.Clamp(settings.LibraryScanIntervalHours, 3, 12));
                    _logger.LogInformation("Scheduling rolling library scan every {Hours}h...", scanEvery.TotalHours);
                    await jobManagement.ScheduleRecurringJobAsync(JobType.LibraryScan, (string?)null, null, null, false, scanEvery, Priority.Normal, cancellationToken).ConfigureAwait(false);
                }

                // Enqueue an immediate verification run at startup.
                // Use matching key "VerifyAllSeries" so the dedup check in EnqueueJobAsIsAsync
                // prevents a double-run if the scheduled job already enqueued one.
                _logger.LogWarning("Starting initial series integrity verification at startup. This may take a while depending on the library size and archive file sizes.");
                await jobManagement.EnqueueJobAsync(JobType.VerifyAllSeries, (string?)null, Priority.Low, "VerifyAllSeries", null, null, "Default", cancellationToken).ConfigureAwait(false);

                _workerCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var workerToken = _workerCts.Token;
                _workerTasks.Add(StartWorker<JobQueueHostedService>(workerToken));
                _workerTasks.Add(StartWorker<JobScheduledHostedService>(workerToken));

                // Auto-categorize the library in the background (non-blocking) — one
                // MangaDex country-of-origin lookup per series, relocating only the
                // ones it can confidently place in a different bucket. Fixes the
                // "everything defaulted to Manga" problem without a manual trigger.
                _ = Task.Run(async () =>
                {
                    try
                    {
                        // Let the initial verify/scan settle first.
                        await Task.Delay(TimeSpan.FromMinutes(1), workerToken).ConfigureAwait(false);
                        using var catScope = _scopeFactory.CreateScope();
                        var maint = catScope.ServiceProvider.GetRequiredService<Series.CategoryMaintenanceService>();
                        // Dry run: only LOG what it would re-file. Auto-moving existing
                        // folders on a MangaDex guess proved too error-prone; the owner
                        // applies real moves on demand via POST /api/serie/recategorize.
                        await maint.RecategorizeAllAsync(null, dryRun: true, workerToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _logger.LogWarning(ex, "Startup auto-categorization pass failed"); }
                }, workerToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting Startup Hosted Service");
                throw;
            }
        }

        private Task StartWorker<TWorker>(CancellationToken workerToken) where TWorker : IWorkerService
        {
            var task = Task.Run(async () =>
            {
                using var scope = _scopeFactory.CreateScope();
                var worker = scope.ServiceProvider.GetRequiredService<TWorker>();
                try
                {
                    await worker.ExecuteAsync(workerToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Worker crashed");
                }
            });
            return task;
        }
        public async Task StopAsync(CancellationToken cancellationToken)
        {
            if (_workerCts == null)
                return;

            _workerCts.Cancel();

            try
            {
                // Use a dedicated 30-second timeout independent of the host's cancellation token
                // so workers have time to drain in-flight work even if the host cancels early.
                await Task.WhenAll(_workerTasks).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                _logger.LogInformation("All background workers stopped gracefully.");
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Background workers did not stop within the 30-second shutdown timeout.");
            }
            catch (OperationCanceledException)
            {
                // Swallow: host is shutting down, some workers may have been cancelled
            }
            finally
            {
                _workerCts.Dispose();
                _workerCts = null;
                _workerTasks.Clear();
            }
        }
    }
}