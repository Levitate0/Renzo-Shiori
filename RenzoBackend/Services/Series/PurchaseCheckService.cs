using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Reader;

namespace RenzoBackend.Services.Series;

/// <summary>
/// "Check purchases": asks the source, with the owner's site login, whether each
/// still-locked chapter of a series is now owned. For a bulk purchase on the
/// site, which otherwise only shows up chapter by chapter (on open, or after the
/// 12-hour paywall backoff). An owned chapter is unlocked and queued.
///
/// Each chapter goes through the reader's own page fetch, so it gets the same
/// re-login, vcomics purchased-pages and "withheld vs failed" rules, and the same
/// unlock, which also queues the download. One check per series at a time; it runs in the background
/// (a long series outlasts the tunnel's 100-second request limit) and the
/// clients poll <see cref="Status"/>.
/// </summary>
public class PurchaseCheckService
{
    public sealed class Progress
    {
        public bool Running { get; set; }
        public int Total { get; set; }
        public int Checked { get; set; }
        public int Owned { get; set; }
        public int StillLocked { get; set; }
        /// <summary>Couldn't tell: the source timed out or errored.</summary>
        public int Failed { get; set; }
        public string? Error { get; set; }
        public DateTime? FinishedAt { get; set; }
    }

    private readonly ConcurrentDictionary<Guid, Progress> _runs = new();
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<PurchaseCheckService> _logger;

    public PurchaseCheckService(IServiceScopeFactory scopes, ILogger<PurchaseCheckService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    public Progress? Status(Guid seriesId) => _runs.TryGetValue(seriesId, out Progress? p) ? p : null;

    /// <summary>Starts a check, or returns the one already running for this series.</summary>
    public async Task<Progress> StartAsync(Guid seriesId, CancellationToken token)
    {
        if (_runs.TryGetValue(seriesId, out Progress? running) && running.Running)
            return running;

        using IServiceScope scope = _scopes.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        SeriesEntity? series = await db.Series.AsNoTracking().Include(s => s.Sources)
            .FirstOrDefaultAsync(s => s.Id == seriesId, token).ConfigureAwait(false);
        if (series == null)
            return new Progress { Error = "Series not found" };

        // Only sources the owner is signed in to: without a session the answer is
        // already known (still paid), and asking would only spend requests.
        HashSet<string> signedIn = (await db.SiteCredentials.AsNoTracking()
                .Where(c => (c.Status == "ok" || c.Status == "manual_cookie")
                            && (series.OwnerId == Guid.Empty || c.UserId == series.OwnerId))
                .Select(c => c.Provider).ToListAsync(token).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SeriesProviderEntity> sources = series.Sources
            .Where(p => !p.IsDisabled && !p.IsUninstalled && !p.IsLocal && signedIn.Contains(p.Provider))
            .ToList();
        if (sources.Count == 0)
            return new Progress { Error = "You aren't signed in to any of this series' sources. Add a site login (Settings → Site logins) first." };

        // A chapter some source already has downloaded needs nothing.
        HashSet<decimal> downloaded = series.Sources.SelectMany(p => p.Chapters)
            .Where(c => c.Number != null && !string.IsNullOrEmpty(c.Filename))
            .Select(c => c.Number!.Value).ToHashSet();
        List<decimal> locked = sources.SelectMany(p => p.Chapters)
            .Where(c => c.Number != null && c.IsLocked && !c.IsDeleted && !downloaded.Contains(c.Number.Value))
            .Select(c => c.Number!.Value).Distinct().OrderBy(n => n).ToList();

        var progress = new Progress { Running = locked.Count > 0, Total = locked.Count };
        if (locked.Count == 0)
        {
            progress.FinishedAt = DateTime.UtcNow;
            _runs[seriesId] = progress;
            return progress;
        }
        _runs[seriesId] = progress;
        _ = Task.Run(() => RunAsync(seriesId, series.OwnerId, locked, progress));
        return progress;
    }

    private async Task RunAsync(Guid seriesId, Guid ownerId, List<decimal> numbers, Progress progress)
    {
        try
        {
            bool first = true;
            foreach (decimal n in numbers)
            {
                using IServiceScope scope = _scopes.CreateScope();
                ReaderPreviewService reader = scope.ServiceProvider.GetRequiredService<ReaderPreviewService>();
                PreviewPagesDto? r = null;
                try
                {
                    // forceRefresh: a cached "locked" from before the purchase must
                    // not answer. The chapter LIST is re-read once, not per chapter:
                    // it is the expensive call and one fresh copy covers them all.
                    r = await reader.GetLibraryStreamPagesAsync(seriesId, n, ownerId == Guid.Empty ? null : ownerId,
                        forceRefresh: true, refreshChapterList: first, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Purchase check: chapter {Chapter} of {SeriesId} failed", n, seriesId);
                }
                first = false;

                if (r is { PageCount: > 0 }) progress.Owned++;
                else if (r is { Locked: true }) progress.StillLocked++;
                else progress.Failed++;
                progress.Checked++;
            }

            _logger.LogInformation(
                "Purchase check for series {SeriesId}: {Owned} owned, {Locked} still paid, {Failed} couldn't be checked.",
                seriesId, progress.Owned, progress.StillLocked, progress.Failed);
        }
        catch (Exception ex)
        {
            progress.Error = "The check stopped early.";
            _logger.LogWarning(ex, "Purchase check for series {SeriesId} stopped", seriesId);
        }
        finally
        {
            progress.FinishedAt = DateTime.UtcNow;
            progress.Running = false;
        }
    }
}
