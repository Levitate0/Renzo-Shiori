using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Jobs.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace RenzoBackend.Services.Jobs.Settings
{
    public class JobsSettings
    {
        // MaxRetries was 150 with a 5-minute RetryTimeSpan = ~12.5 HOURS of retrying per job. With
        // "download all chapters" on, every unattainable chapter (paid without a site login, a
        // Cloudflare site the solver can't crack, a dead host) retried ~150x every 5 min — a sustained
        // background churn (each retry = a sidecar page fetch + CF solve) that pinned CPU and starved
        // the API on modest hosts. 8 retries (~40 min) still recovers transient CF/network hiccups but
        // gives up quickly on the permanently-broken ones; re-queue succeeds once the blocker is fixed.
        // Downloads run 16-wide (was 10) to drain the backlog faster — they are I/O-bound (network +
        // Cloudflare solve), so extra concurrency mostly fills idle wait time. MaxPerGroup stays at 3,
        // so no single source is hit harder; the extra width just downloads from more sources at once.
        // Keep this <= the sidecar HTTP pool (RENZO_SIDECAR_THREADS, default 16, raised to 32 in the
        // image env) minus the Default queue's 10, so interactive API calls always have sidecar threads.
        private List<QueueSettings> _queueThreadLimits = new List<QueueSettings>()
        {
            new QueueSettings(JobQueues.Default, 10, 8),
            new QueueSettings(JobQueues.Downloads, 16, 8)
        };

        public List<QueueSettings> GetQueueSettings()
        {
            return _queueThreadLimits.ToList();
        }
        public void SetQueueSettings(JobQueues queue, int maxThreads, int retries, int maxPerGroup, TimeSpan? span = null)
        {
            QueueSettings settings = _queueThreadLimits.First(a => a.Name == queue);
            settings.MaxThreads = maxThreads;
            settings.MaxRetries = retries;
            settings.MaxPerGroup = maxPerGroup;
            if (span!=null)
                settings.RetryTimeSpan = span.Value;
        }

        public TimeSpan QueuePollingInterval { get; set; } = TimeSpan.FromMilliseconds(500);
        public TimeSpan JobsPollingInterval { get; set; } = TimeSpan.FromMilliseconds(500);

        public Dictionary<JobType, TimeSpan> JobTimes = new Dictionary<JobType, TimeSpan>()
        {
            { JobType.UpdateExtensions, TimeSpan.FromHours(1)},
            { JobType.GetChapters, TimeSpan.FromHours(2)},
            { JobType.GetLatest, TimeSpan.FromMinutes(30)},
            { JobType.DailyUpdate,TimeSpan.FromDays(1)}
        };
    }
}
