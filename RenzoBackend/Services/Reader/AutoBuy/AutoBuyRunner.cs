using System.Collections.Concurrent;
using RenzoBackend.Models.Database;

namespace RenzoBackend.Services.Reader.AutoBuy;

/// <summary>
/// Runs <see cref="ChapterAutoBuyService"/> in the background: a purchase that
/// has to sign the browser in first outlasts the tunnel's 100-second request
/// limit. The reader starts it and polls <see cref="Status"/>.
/// </summary>
public class AutoBuyRunner
{
    private readonly ConcurrentDictionary<(Guid, decimal), AutoBuyResultDto> _runs = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<AutoBuyRunner> _logger;

    public AutoBuyRunner(IServiceScopeFactory scopes, ILogger<AutoBuyRunner> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public AutoBuyResultDto? Status(Guid seriesId, decimal chapter) =>
        _runs.TryGetValue((seriesId, chapter), out AutoBuyResultDto? r) ? r : null;

    /// <summary>Starts a purchase, or returns the one already running for this chapter.</summary>
    public AutoBuyResultDto Start(Guid seriesId, decimal chapter, UserEntity? requester, bool dryRun)
    {
        var running = new AutoBuyResultDto { Status = "running" };
        var key = (seriesId, chapter);
        AutoBuyResultDto current = _runs.AddOrUpdate(key, running,
            (_, old) => old.Status == "running" ? old : running);
        if (!ReferenceEquals(current, running))
            return current;

        _ = Task.Run(async () =>
        {
            try
            {
                using IServiceScope scope = _scopes.CreateScope();
                var buyer = scope.ServiceProvider.GetRequiredService<ChapterAutoBuyService>();
                _runs[key] = await buyer.TryBuyAsync(seriesId, chapter, requester, CancellationToken.None, dryRun).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Auto-buy failed for {SeriesId} ch {Chapter}", seriesId, chapter);
                _runs[key] = new AutoBuyResultDto
                {
                    Status = "failed", Reason = "unavailable",
                    Message = "Something went wrong while buying this chapter automatically.",
                };
            }
        });
        return running;
    }
}
