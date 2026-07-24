using System.Diagnostics.CodeAnalysis;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Series;

namespace RenzoBackend.Services.Jobs.Commands;

public class UpdateAllSeries : ICommand
{
    public JobType JobType => JobType.UpdateAllSeries;
    public Type? ParameterType => null;
    private readonly SeriesArchiveService _archiveService;
    
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(UpdateAllSeries))]
    public UpdateAllSeries(SeriesArchiveService archiveService)
    {
        _archiveService = archiveService;
    }

    public Task<JobResult> ExecuteAsync(JobInfo job, CancellationToken token = default)
    {
        return _archiveService.UpdateAllSeriesAsync(job, token);
    }
}