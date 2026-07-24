using System.Diagnostics.CodeAnalysis;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Series;

namespace RenzoBackend.Services.Jobs.Commands;

/// <summary>
/// Library-wide new-chapter scan. Backs the "Update now" button and the
/// configurable rolling scan (every 3–12 h): fans out a GetChapters job per
/// active provider, which discovers new chapters, stamps their found time,
/// and queues downloads.
/// </summary>
public class LibraryScan : ICommand
{
    public JobType JobType => JobType.LibraryScan;
    public Type? ParameterType => null;
    private readonly SeriesCommandService _commandService;

    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(LibraryScan))]
    public LibraryScan(SeriesCommandService commandService)
    {
        _commandService = commandService;
    }

    public Task<JobResult> ExecuteAsync(JobInfo job, CancellationToken token = default)
    {
        return _commandService.ScanAllSeriesAsync(token);
    }
}
