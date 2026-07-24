using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Jobs.Models;
using RenzoBackend.Services.Providers;
using System.Diagnostics.CodeAnalysis;

namespace RenzoBackend.Services.Jobs.Commands;

public class UpdateExtensions : ICommand
{
    public JobType JobType => JobType.UpdateExtensions;
    public Type? ParameterType => null;

    private readonly ProviderCacheService _cache;
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(UpdateExtensions))]

    public UpdateExtensions(ProviderCacheService cache)
    {
        _cache = cache;
    }
    
    public async Task<JobResult> ExecuteAsync(JobInfo job, CancellationToken token = default)
    {
        try
        {
            // Get all cached providers and check for updates
            await _cache.UpdateAllExtensionsAsync(token).ConfigureAwait(false);
            return JobResult.Success;
        }
        catch (Exception)
        {
            return JobResult.Failed;
        }
    }
}