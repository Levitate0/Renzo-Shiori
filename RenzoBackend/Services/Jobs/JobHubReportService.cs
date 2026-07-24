using RenzoBackend.Extensions;
using RenzoBackend.Hubs;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Jobs.Report;
using Microsoft.AspNetCore.SignalR;

namespace RenzoBackend.Services.Jobs;

/// <summary>
/// Unified service for reporting both job state changes and progress updates via SignalR
/// </summary>
public class JobHubReportService : IReportProgress
{
    private readonly IHubContext<ProgressHub> _hub;

    public JobHubReportService(IHubContext<ProgressHub> hub)
    {
        _hub = hub;
    }

    /// <summary>
    /// Reports job state changes (queued, running, completed, failed) to SignalR clients
    /// </summary>
    /// <param name="state">The job queue state</param>
    /// <param name="token">Cancellation token</param>
    /// <returns>Task representing the async operation</returns>
    /*
    public Task ReportJobAsync(Enqueue state, CancellationToken token = default)
    {
        return _hub.Clients.All.SendAsync("Jobs", state.ToJobState(), token);
    }
    */

    // Last broadcast per job type, so pollers (e.g. the import wizard's
    // fallback status poll) can show progress even when a client's SignalR
    // connection is unavailable. SignalR has no replay: a client that
    // (re)connects mid-job would otherwise sit at 0% until the next event.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<RenzoBackend.Models.Enums.JobType, ProgressState> _lastProgress = new();

    /// <summary>
    /// Returns the most recent progress broadcast for a job type (process
    /// lifetime), or null when none has been sent yet.
    /// </summary>
    public static ProgressState? GetLastProgress(RenzoBackend.Models.Enums.JobType jobType) =>
        _lastProgress.TryGetValue(jobType, out ProgressState? state) ? state : null;

    /// <summary>
    /// Reports job progress updates to SignalR clients
    /// </summary>
    /// <param name="state">The progress state</param>
    /// <returns>Task representing the async operation</returns>
    public Task ReportProgressAsync(ProgressState state)
    {
        _lastProgress[state.JobType] = state;
        // Per-user library isolation: a job tied to a specific owner (currently
        // Download jobs — see DownloadCommandService) only broadcasts to that
        // owner's connections plus Owner-level accounts (who can already view
        // every library elsewhere). Jobs with no owner (imports, library-wide
        // scans, etc.) keep broadcasting to everyone, same as before.
        if (state.OwnerId is { } ownerId && ownerId != Guid.Empty)
        {
            return Task.WhenAll(
                _hub.Clients.Group(ProgressHub.UserGroup(ownerId)).SendAsync("Progress", state),
                _hub.Clients.Group(ProgressHub.OwnersGroup).SendAsync("Progress", state)
            );
        }
        return _hub.Clients.All.SendAsync("Progress", state);
    }

    /// <summary>
    /// Creates a progress reporter for a specific job
    /// </summary>
    /// <param name="job">Job information</param>
    /// <param name="ownerId">The series owner this job's progress belongs to, if any — scopes the live broadcast to just them (plus Owner-level accounts).</param>
    /// <returns>Progress reporter instance</returns>
    public ProgressReporter CreateReporter(JobInfo job, Guid? ownerId = null)
    {
        return new ProgressReporter(this, job, ownerId);
    }
}