using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Jobs;
using Microsoft.EntityFrameworkCore;
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using RenzoBackend.Extensions;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Jobs.Settings;
using RenzoBackend.Models.Enums;

namespace RenzoBackend.Services.Background
{
    public interface IWorkerService
    {
        Task ExecuteAsync(CancellationToken stoppingToken);
    }
    public class JobQueueHostedService : IWorkerService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<JobQueueHostedService> _logger;
        private readonly JobsSettings _settings;
        private readonly ConcurrentDictionary<JobQueues, ConcurrentDictionary<string, byte>> _runningJobs = new();
        private readonly object _slotLock = new object();
        private readonly ConcurrentBag<Task> _inFlightJobTasks = new();
        // Rotates which GroupKey gets first pick of a queue's limited slots each tick.
        // Without this, when there are more distinct groups (providers) than
        // availableSlots, the group order feeding FairShareOrderBy is stable
        // (DB fetch order), so Take(availableSlots) always cuts off after the
        // same first few groups — every group past that point starves forever,
        // even though it has eligible Waiting jobs. Rotating the starting group
        // each tick guarantees every group eventually lands inside the window.
        private readonly ConcurrentDictionary<JobQueues, int> _groupRotation = new();

        /// <summary>
        /// Hard ceiling on a single job. Generous — a big chapter over a slow
        /// source is legitimately minutes — but finite, so a job that will never
        /// finish cannot hold its group's only slot indefinitely.
        /// </summary>
        private static readonly TimeSpan MaxJobDuration = TimeSpan.FromMinutes(20);

        public JobQueueHostedService(IServiceScopeFactory scopeFactory, ILogger<JobQueueHostedService> logger,
            JobsSettings settings)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _settings = settings;

            // Initialize running jobs tracking
            foreach (var queue in _settings.GetQueueSettings())
            {
                _runningJobs[queue.Name] = new ConcurrentDictionary<string, byte>();
            }
        }

        public async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Job Queue Service is starting");
            
            using (var scope = _scopeFactory.CreateScope())
            {
                var jobManagement = scope.ServiceProvider.GetRequiredService<JobManagementService>();
                await jobManagement.StartupAsync(stoppingToken).ConfigureAwait(false);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ProcessJobQueuesAsync(stoppingToken).ConfigureAwait(false);
                    await Task.Delay(_settings.QueuePollingInterval, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing job queues");
                }
            }

            _logger.LogInformation("Job Queue Service is stopping. Waiting for {Count} in-flight jobs to complete...", _inFlightJobTasks.Count);
            
            // Drain in-flight jobs with a bounded timeout to prevent shutdown hang
            try
            {
                await Task.WhenAll(_inFlightJobTasks).WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                _logger.LogInformation("All in-flight jobs completed successfully.");
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("Some in-flight jobs did not complete within the 30-second shutdown timeout.");
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown - some jobs were cancelled
            }
        }

        private async Task ProcessJobQueuesAsync(CancellationToken stoppingToken)
        {
            using var scope = _scopeFactory.CreateScope();
            var jobManagement = scope.ServiceProvider.GetRequiredService<JobManagementService>();

            foreach (var queueEntry in _settings.GetQueueSettings())
            {
                await ProcessQueueAsync(jobManagement, queueEntry, stoppingToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Frees the slot of a job that has been Running past the deadline.
        ///
        /// The deadline on the job task is not enough on its own, because
        /// cancellation is COOPERATIVE: a job blocked somewhere that never checks
        /// its token simply ignores it. Measured directly — twelve jobs sat at 24
        /// minutes against a 20-minute deadline without a single one being
        /// abandoned. A provider gets one download slot, so each of those blocked
        /// its source outright, and the only thing that ever cleared them was a
        /// restart (StartupAsync resets Running rows).
        ///
        /// So the ROW is reclaimed whether or not the task ever returns. The
        /// orphaned task may still be sitting there, but it is producing nothing;
        /// the worst case is one chapter downloaded twice, against a source that
        /// would otherwise be blocked indefinitely.
        /// </summary>
        private async Task ReclaimStuckJobsAsync(JobManagementService jobManagement, QueueSettings queueSettings,
            ConcurrentDictionary<string, byte> runningJobsInQueue, CancellationToken stoppingToken)
        {
            DateTime cutoff = DateTime.UtcNow - MaxJobDuration;
            List<EnqueueEntity> stuck = await jobManagement.QueuedJobs
                .Where(j => j.Queue == queueSettings.Name.ToString()
                            && j.Status == QueueStatus.Running
                            && j.StartedDate != null
                            && j.StartedDate < cutoff)
                .ToListAsync(stoppingToken).ConfigureAwait(false);
            if (stuck.Count == 0)
                return;

            foreach (EnqueueEntity job in stuck)
            {
                runningJobsInQueue.TryRemove(job.Id.ToString(), out _);
                await jobManagement.QueuedJobs.Where(j => j.Id == job.Id)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(j => j.Status, QueueStatus.Waiting)
                        .SetProperty(j => j.StartedDate, (DateTime?)null)
                        .SetProperty(j => j.RetryCount, job.RetryCount + 1)
                        // A short delay, so a job that wedges every time cannot
                        // spin the queue.
                        .SetProperty(j => j.ScheduledDate, DateTime.UtcNow.AddMinutes(5)),
                        stoppingToken).ConfigureAwait(false);
            }

            _logger.LogWarning(
                "Reclaimed {Count} job slot(s) in queue {Queue} held by jobs Running past {Minutes} minutes ({Groups}).",
                stuck.Count, queueSettings.Name, MaxJobDuration.TotalMinutes,
                string.Join(", ", stuck.Select(j => j.GroupKey).Distinct()));
        }

        private async Task ProcessQueueAsync(JobManagementService jobManagement, QueueSettings queueSettings, 
            CancellationToken stoppingToken)
        {
            var queueName = queueSettings.Name;
            var runningJobsInQueue = _runningJobs.GetOrAdd(queueName, _ => new ConcurrentDictionary<string, byte>());

            // Before anything else, take back slots held by jobs that are never
            // going to finish — otherwise the checks below see a full queue.
            await ReclaimStuckJobsAsync(jobManagement, queueSettings, runningJobsInQueue, stoppingToken).ConfigureAwait(false);

            // Check available slots outside lock first (quick exit optimization)
            if (runningJobsInQueue.Count >= queueSettings.MaxThreads)
                return;

            var availableSlots = queueSettings.MaxThreads - runningJobsInQueue.Count;
            if (availableSlots <= 0)
                return;

            // Get jobs ready for execution
            var jobsToProcess = await GetJobsToProcessAsync(jobManagement, queueName, queueSettings, 
                availableSlots, stoppingToken).ConfigureAwait(false);

            foreach (var job in jobsToProcess)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                // Atomic slot allocation: check and reserve under lock
                lock (_slotLock)
                {
                    if (runningJobsInQueue.Count >= queueSettings.MaxThreads)
                        break;

                    if (!runningJobsInQueue.TryAdd(job.Id.ToString(), 0))
                        continue; // Job already running, skip
                }

                // Update job status to running
                job.Status = QueueStatus.Running;
                job.StartedDate = DateTime.UtcNow;
                
                // Save changes through the service
                await UpdateJobStatusAsync(job, stoppingToken).ConfigureAwait(false);
                jobManagement.DetachJob(job);
                
                // Track in-flight job task so we can drain on shutdown
                var jobTask = ExecuteJobAsync(job, queueName, queueSettings, stoppingToken);
                _inFlightJobTasks.Add(jobTask);
            }
        }

        /// <summary>
        /// Orders one group's jobs so a series downloads lowest chapter first.
        ///
        /// Within a group the queue used to take jobs in whatever order the
        /// database returned them, which is insertion order — and a series'
        /// chapters are enqueued newest first, so a freshly added series fetched
        /// 203, 202, 201... and the chapter the reader actually starts on came
        /// last. Reading order is the useful one: the first chapters are ready
        /// soonest.
        ///
        /// Only the order WITHIN a series changes. Series keep the order they
        /// already had (first appearance), and FairShareOrderBy still
        /// interleaves the groups afterwards, so no series or provider gains or
        /// loses its turn. Non-download jobs, or a key whose last segment is not
        /// a chapter number, keep their original position.
        /// </summary>
        private static IEnumerable<EnqueueEntity> SeriesInReadingOrder(IEnumerable<EnqueueEntity> jobs)
        {
            List<EnqueueEntity> list = jobs.ToList();
            if (list.Count < 2)
                return list;

            var seriesRank = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (EnqueueEntity j in list)
                seriesRank.TryAdd(j.ExtraKey ?? string.Empty, seriesRank.Count);

            return list
                .Select((job, index) => (job, index))
                .OrderBy(x => seriesRank[x.job.ExtraKey ?? string.Empty])
                .ThenBy(x => ChapterNumberOf(x.job) ?? decimal.MaxValue)
                .ThenBy(x => x.index)
                .Select(x => x.job);
        }

        /// <summary>The chapter number a download job's key ends with, or null.</summary>
        private static decimal? ChapterNumberOf(EnqueueEntity job)
        {
            if (job.JobType != JobType.Download || string.IsNullOrEmpty(job.Key))
                return null;
            int bar = job.Key.LastIndexOf('|');
            if (bar < 0 || bar == job.Key.Length - 1)
                return null;
            return decimal.TryParse(job.Key.AsSpan(bar + 1), System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out decimal n) ? n : null;
        }

        private async Task<List<EnqueueEntity>> GetJobsToProcessAsync(JobManagementService jobManagement, JobQueues queueName,
            QueueSettings queueSettings, int availableSlots, CancellationToken stoppingToken)
        {
            // Get running job counts by group
            var runningCounts = await jobManagement.QueuedJobs
                .Where(a => a.Status == QueueStatus.Running)
                .GroupBy(a => a.GroupKey)
                .ToDictionaryAsync(a => a.Key, a => a.Count(), stoppingToken);

            // Get waiting jobs for this queue
            var waitingJobs = await jobManagement.QueuedJobs
                .Where(j => j.Queue == queueName.ToString() && 
                           j.Status == QueueStatus.Waiting && 
                           j.ScheduledDate <= DateTime.UtcNow)
                .OrderByDescending(j => j.Priority)
                .ToListAsync(stoppingToken);

            // Apply group limits and fair sharing
            var jobsByPriority = waitingJobs.GroupBy(j => j.Priority).ToDictionary(g => g.Key, g => g.ToList());

            foreach (Priority priority in jobsByPriority.Keys)
            {
                var groups = jobsByPriority[priority].GroupBy(a => a.GroupKey).ToList();

                // Rotate the starting group so a later Take(availableSlots) doesn't
                // always cut off after the same groups — see _groupRotation comment.
                int offset = groups.Count == 0 ? 0 : _groupRotation.AddOrUpdate(queueName, 0, (_, v) => v + 1) % groups.Count;
                var rotatedGroups = groups.Skip(offset).Concat(groups.Take(offset));

                var groupedJobs = rotatedGroups
                    .ToDictionary(g => g.Key, g => SeriesInReadingOrder(g).Take(runningCounts.GetLocalGroupMax(g.Key, queueSettings.MaxPerGroup)).ToList());

                jobsByPriority[priority] = groupedJobs.SelectMany(a => a.Value).FairShareOrderBy(a => a.GroupKey).ToList();
            }

            return jobsByPriority.SelectMany(a => a.Value).Take(availableSlots).ToList();
        }

        private async Task UpdateJobStatusAsync(EnqueueEntity job, CancellationToken stoppingToken)
        {
            // Update job status through database context
            using var scope = _scopeFactory.CreateScope();
            var management = scope.ServiceProvider.GetRequiredService<JobManagementService>();
            // This uses the internal update method
            await management.QueuedJobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(updates => updates.SetProperty(j => j.Status, QueueStatus.Running)
                    .SetProperty(j => j.StartedDate, DateTime.UtcNow), stoppingToken);
        }

        private async Task ExecuteJobAsync(EnqueueEntity job, JobQueues queueName, QueueSettings queueSettings,
            CancellationToken stoppingToken)
        {
            var jobId = job.Id.ToString();
            
            using var scope = _scopeFactory.CreateScope();
            var jobExecution = scope.ServiceProvider.GetRequiredService<JobExecutionService>();

            // Every job gets a deadline.
            //
            // A source call is individually bounded (SourceTimeout), but the JOB
            // around them was not, and a job that never returns holds its group's
            // slot forever. With one download slot per provider that is not a
            // slowdown, it is a permanent block: twelve jobs were found sitting
            // Running for 2h48m — since the moment the container started — while
            // 8,263 chapters waited behind them and nothing was logged, because
            // hanging is not an error. This is the "queue wedges, only a restart
            // clears it" behaviour, and the restart only cleared it because
            // StartupAsync resets Running rows.
            //
            // The deadline turns a hang into an ordinary failure: the slot is
            // released and the job retries like any other.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            deadline.CancelAfter(MaxJobDuration);

            try
            {
                //_logger.LogInformation("Starting job {Key} in queue {queueName}", job.Key, queueName);
                
                JobInfo jobInfo = new JobInfo(job.Id, job.JobType, job.Key, job.GroupKey, job.JobParameters);
                JobResult result = await jobExecution.ExecuteJobAsync(jobInfo, deadline.Token).ConfigureAwait(false);
                
                await HandleJobResultAsync(job, result, queueName, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                // The deadline fired, not a shutdown. Treat it as a failure so the
                // job is retried rather than silently abandoned.
                _logger.LogWarning(
                    "Job {Key} ({Group}) in queue {queueName} exceeded {Minutes} minutes and was abandoned so its slot could be reused.",
                    job.Key, job.GroupKey, queueName, MaxJobDuration.TotalMinutes);
                try
                {
                    await HandleJobFailureAsync(job, queueSettings, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown — job was cancelled, no need to log as error
            }
            catch (ObjectDisposedException)
            {
                // DI container is being disposed during shutdown — job scope creation failed
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error executing job {Key} in queue {queueName}", job.Key, queueName);
                // Attempt graceful failure handling; if it fails due to shutdown, just swallow
                try
                {
                    await HandleJobFailureAsync(job, queueSettings, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }
            finally
            {
                // Remove job from running list
                if (_runningJobs.TryGetValue(queueName, out var runningJobs))
                {
                    runningJobs.TryRemove(jobId, out _);
                }
            }
        }

        private async Task HandleJobResultAsync(EnqueueEntity job, JobResult result, JobQueues queueName,
            CancellationToken stoppingToken)
        {
            // Guard against disposed container during shutdown
            if (stoppingToken.IsCancellationRequested)
                return;

            using var scope = _scopeFactory.CreateScope();
            var management = scope.ServiceProvider.GetRequiredService<JobManagementService>();
            
            if (result != JobResult.Handled)
            {
                var updatedJob = await management.QueuedJobs.FirstAsync(a => a.Id == job.Id, stoppingToken);
                if (updatedJob != null)
                {
                    updatedJob.Status = result == JobResult.Success ? QueueStatus.Completed : QueueStatus.Failed;
                    updatedJob.FinishedDate = DateTime.UtcNow;
                    /*
                    if (result == JobResult.Success)
                    {
                        _logger.LogInformation("Completed job {Key} in queue {queueName}", job.Key, queueName);
                    }
                    else
                    {
                        _logger.LogWarning("Failed job {Key} in queue {queueName}", job.Key, queueName);
                    }
                    */
                    await management.QueuedJobs.Where(j => j.Id == job.Id)
                        .ExecuteUpdateAsync(updates => updates.SetProperty(j => j.Status, updatedJob.Status)
                            .SetProperty(j => j.FinishedDate, updatedJob.FinishedDate), stoppingToken);
                            
                    if (result == JobResult.Delete)
                    {
                        // Extract the recurring job key from the enqueued job's key.
                        // The enqueued job key is formatted as "{JobType}_{key}" by EnqueueJobAsIsAsync.
                        // The recurring job key is the suffix after "{JobType}_".
                        // e.g., enqueued key "GetChapters_d0e179a7-1814..." -> recurring key "d0e179a7-1814..."
                        string prefix = $"{job.JobType}_";
                        string recurringKey = job.Key.StartsWith(prefix)
                            ? job.Key.Substring(prefix.Length)
                            : job.Key;
                        await management.DeleteRecurringJobAsync(job.JobType, recurringKey, stoppingToken);
                    }
                }
            }
            else
            {
                _logger.LogWarning("Rescheduled job {jobType} {GroupKey} in queue {queueName}", job.JobType, job.GroupKey ?? job.Key, queueName);
            }
        }

        private async Task HandleJobFailureAsync(EnqueueEntity job, QueueSettings queueSettings, CancellationToken stoppingToken)
        {
            // Guard against disposed container during shutdown
            if (stoppingToken.IsCancellationRequested)
                return;

            using var scope = _scopeFactory.CreateScope();
            var management = scope.ServiceProvider.GetRequiredService<JobManagementService>();
            
            var updatedJob = await management.QueuedJobs.FirstAsync(a => a.Id == job.Id, stoppingToken);
            if (updatedJob != null)
            {
                updatedJob.RetryCount += 1;
                
                if (updatedJob.RetryCount >= queueSettings.MaxRetries)
                {
                    updatedJob.Status = QueueStatus.Failed;
                    updatedJob.FinishedDate = DateTime.UtcNow;
                }
                else
                {
                    updatedJob.Status = QueueStatus.Waiting;
                    updatedJob.ScheduledDate = DateTime.UtcNow.Add(queueSettings.RetryTimeSpan);
                }
                
                await management.QueuedJobs.Where(j => j.Id == job.Id)
                    .ExecuteUpdateAsync(updates => updates.SetProperty(j => j.Status, updatedJob.Status)
                        .SetProperty(j => j.RetryCount, updatedJob.RetryCount)
                        .SetProperty(j => j.FinishedDate, updatedJob.FinishedDate)
                        .SetProperty(j => j.ScheduledDate, updatedJob.ScheduledDate), stoppingToken);
            }
        }
    }
}