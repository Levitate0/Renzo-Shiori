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

    /// <summary>
    /// Starts a purchase, or returns the one already running for this chapter.
    /// The checks that need no browser come first, so auto-buy being off (or a
    /// reason it can't buy) is answered at once: "running" is what the readers
    /// show as "Auto-buy is buying this chapter…", and it used to come back for
    /// every paid chapter, auto-buy on or not, until the first poll said "off".
    /// </summary>
    public async Task<AutoBuyResultDto> StartAsync(Guid seriesId, decimal chapter, UserEntity? requester, bool dryRun, CancellationToken token)
    {
        var key = (seriesId, chapter);
        if (_runs.TryGetValue(key, out AutoBuyResultDto? existing) && existing.Status == "running")
            return existing;
        using (IServiceScope scope = _scopes.CreateScope())
        {
            AutoBuyResultDto? answer = await scope.ServiceProvider.GetRequiredService<ChapterAutoBuyService>()
                .PrecheckAsync(seriesId, chapter, requester, token, dryRun).ConfigureAwait(false);
            if (answer != null)
                return _runs.AddOrUpdate(key, answer, (_, old) => old.Status == "running" ? old : answer);
        }

        var running = new AutoBuyResultDto { Status = "running" };
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
