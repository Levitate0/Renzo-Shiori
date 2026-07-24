using RenzoBackend.Models;

namespace RenzoBackend.Services.Jobs.Models;

public interface IReportProgress
{
    Task ReportProgressAsync(ProgressState state);
}