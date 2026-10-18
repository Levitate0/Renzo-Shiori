using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Jobs.Models;

namespace RenzoBackend.Services.Jobs.Report;

public class ProgressReporter
{
    private readonly IReportProgress _report;
    public IProgress<ProgressState> Progress { get; }
    public JobInfo Job { get; }
    /// <summary>The series owner this job belongs to, if any — see ProgressState.OwnerId.</summary>
    public Guid? OwnerId { get; }

    // Reports go out strictly in the order they were made. They used to go
    // through Progress<T>, which hands each one to the thread pool on its own,
    // so a late "page 30 of 31" could reach clients after "completed" and
    // re-add a finished download to their live list, where it then sat for
    // good.
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;
    private ProgressState? _last;

    public ProgressReporter(IReportProgress report, JobInfo job, Guid? ownerId = null)
    {
        _report = report;
        Job = job;
        OwnerId = ownerId;
        Progress = new OrderedProgress(this);
    }

    public void Report(ProgressStatus status, decimal percentage,string? message, DownloadSummary? download = null, string? errorMessage = null)
    {
        Send(new ProgressState
        {
            Id = Job.JobId,
            JobType = Job.JobType,
            ProgressStatus = status,
            Percentage = percentage,
            Message = message ?? "",
            ErrorMessage = errorMessage,
            Download = download?.ToCardInfoDto(),
            OwnerId = OwnerId
        });
    }

    /// <summary>
    /// Closes the run for clients when it ended without saying so — a step-down
    /// to another source, an exception, an early return. Without it the run
    /// stays "in progress" in every open live list until the page is reloaded.
    /// </summary>
    public void End(bool succeeded)
    {
        ProgressState? last;
        lock (_gate)
            last = _last;
        if (last == null || last.ProgressStatus is ProgressStatus.Completed or ProgressStatus.Failed)
            return;
        Send(new ProgressState
        {
            Id = last.Id,
            JobType = last.JobType,
            ProgressStatus = succeeded ? ProgressStatus.Completed : ProgressStatus.Failed,
            Percentage = succeeded ? 100 : last.Percentage,
            Message = last.Message,
            Download = last.Download,
            OwnerId = last.OwnerId
        });
    }

    private void Send(ProgressState state)
    {
        lock (_gate)
        {
            _last = state;
            _tail = _tail.ContinueWith(async _ =>
            {
                try { await _report.ReportProgressAsync(state).ConfigureAwait(false); }
                catch { /* a client that went away must not stop the reports behind it */ }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    private sealed class OrderedProgress(ProgressReporter owner) : IProgress<ProgressState>
    {
        public void Report(ProgressState value) => owner.Send(value);
    }
}
