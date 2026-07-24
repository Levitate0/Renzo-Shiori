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
    public ProgressReporter(IReportProgress report, JobInfo job, Guid? ownerId = null)
    {
        _report = report;
        Job = job;
        OwnerId = ownerId;
        Progress = new Progress<ProgressState>(async state =>
        {
            await _report.ReportProgressAsync(state).ConfigureAwait(false);
        });
    }
    public void Report(ProgressStatus status, decimal percentage,string? message, DownloadSummary? download = null, string? errorMessage = null)
    {
        Progress.Report(new ProgressState
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

}