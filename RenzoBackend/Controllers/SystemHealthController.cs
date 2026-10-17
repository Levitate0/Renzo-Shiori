using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Mihon.ExtensionsBridge.Core.Runtime.Sidecar;
using RenzoBackend.Data;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Background;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Jobs.Settings;
using RenzoBackend.Services.Settings;

namespace RenzoBackend.Controllers;

/// <summary>
/// GET /api/system/health — the live state behind "why isn't anything
/// downloading?": the extension engine (sidecar) and the job queues. Every
/// value here was previously only visible by reading container logs.
/// Owner-only: it describes the server, not a library.
/// </summary>
[ApiController]
[Route("api/system")]
[Produces("application/json")]
public class SystemHealthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly SettingsService _settings;
    private readonly JobsSettings _jobs;
    private readonly JobQueueHostedService _queue;
    private readonly IServiceProvider _services;

    public SystemHealthController(AppDbContext db, SettingsService settings, JobsSettings jobs,
        JobQueueHostedService queue, IServiceProvider services)
    {
        _db = db;
        _settings = settings;
        _jobs = jobs;
        _queue = queue;
        _services = services;
    }

    [HttpGet("health")]
    [RequireUserLevel(UserLevel.Owner)]
    public async Task<ActionResult> GetHealth(CancellationToken token = default)
    {
        var settings = await _settings.GetSettingsAsync(token).ConfigureAwait(false);
        DateTime now = DateTime.UtcNow;

        object? engine = null;
        if (_services.GetService(typeof(SidecarProcessManager)) is SidecarProcessManager mgr)
        {
            using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
            probe.CancelAfter(TimeSpan.FromSeconds(3));
            SidecarClient.SidecarStatus? st = await mgr.GetStatusAsync(probe.Token).ConfigureAwait(false);
            (int live, int zombies, int limit) = mgr.GetHelperCounts();
            engine = new
            {
                running = mgr.IsReady,
                responding = st != null,
                busyWorkers = st?.Busy,
                workers = st?.Workers,
                lastCompletedSecondsAgo = st == null ? (long?)null : st.LastCompletedAgoMs / 1000,
                heapUsedMb = st?.HeapUsedMb,
                heapMaxMb = st?.HeapMaxMb,
                browserHelpers = live,
                browserHelperLimit = limit,
                zombieHelpers = zombies,
                startedAt = mgr.StartedAt,
                restartsSinceAppStart = mgr.RestartCount,
                lastRestartAt = mgr.LastRestartAt,
                lastRestartReason = mgr.LastRestartReason,
            };
        }

        // One grouped query instead of one per status.
        var counts = await _db.Queues.AsNoTracking()
            .Where(q => q.JobType == JobType.Download)
            .GroupBy(q => new { q.Status, Due = q.ScheduledDate <= now })
            .Select(g => new { g.Key.Status, g.Key.Due, Count = g.Count() })
            .ToListAsync(token).ConfigureAwait(false);
        int Count(QueueStatus s, bool? due = null) =>
            counts.Where(c => c.Status == s && (due == null || c.Due == due)).Sum(c => c.Count);

        QueueSettings? dl = _jobs.GetQueueSettings().FirstOrDefault(q => q.Name == JobQueues.Downloads);
        QueueSettings? df = _jobs.GetQueueSettings().FirstOrDefault(q => q.Name == JobQueues.Default);

        return Ok(new
        {
            engine,
            downloads = new
            {
                slotsInUse = _queue.HeldSlots(JobQueues.Downloads),
                maxSlots = dl?.MaxThreads ?? settings.NumberOfSimultaneousDownloads,
                perSource = dl?.MaxPerGroup ?? settings.NumberOfSimultaneousDownloadsPerProvider,
                waitingDue = Count(QueueStatus.Waiting, due: true),
                waitingLater = Count(QueueStatus.Waiting, due: false),
                running = Count(QueueStatus.Running),
                failed = Count(QueueStatus.Failed),
                retries = settings.ChapterDownloadFailRetries,
                retryEveryMinutes = (int)settings.ChapterDownloadFailRetryTime.TotalMinutes,
                autoDownloadNewChapters = settings.AutoDownloadNewChapters,
            },
            background = new
            {
                slotsInUse = _queue.HeldSlots(JobQueues.Default),
                maxSlots = df?.MaxThreads ?? 10,
            },
        });
    }
}
