using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Daily;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Jobs.Models;

namespace RenzoBackend.Services.Jobs.Commands
{
    public class DailyUpdate : ICommand
    {
        public JobType JobType => JobType.DailyUpdate;
        public Type? ParameterType => null;
        private readonly DailyService _dailyService;

        [DynamicDependency(DynamicallyAccessedMemberTypes.PublicConstructors, typeof(DailyUpdate))]
        public DailyUpdate(DailyService dailyService)
        {
            _dailyService = dailyService;
        }

        public Task<JobResult> ExecuteAsync(JobInfo job, CancellationToken token = default)
        {
            return _dailyService.ExecuteAsync(job, token);
        }

    }
}
