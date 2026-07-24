using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Import;
using RenzoBackend.Services.Jobs.Models;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace RenzoBackend.Services.Jobs.Commands;

public class ScanLocalFiles : ICommand
{
    public JobType JobType => JobType.ScanLocalFiles;
    public Type? ParameterType => typeof(ScanLocalFilesParameters);
    private readonly ImportCommandService _service;
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(ScanLocalFiles))]
    public ScanLocalFiles(ImportCommandService service)
    {
        _service = service;
    }

    public async Task<JobResult> ExecuteAsync(JobInfo job, CancellationToken token = default)
    {
        if (job.Parameters == null)
            return JobResult.Failed;
        ScanLocalFilesParameters? parameters = JsonSerializer.Deserialize<ScanLocalFilesParameters>(job.Parameters);
        if (parameters == null || string.IsNullOrEmpty(parameters.Path))
            return JobResult.Failed;
        return await _service.ScanAsync(parameters.Path, job, parameters.TitleOnly, token).ConfigureAwait(false);
    }
}