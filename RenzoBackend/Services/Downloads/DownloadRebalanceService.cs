using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RenzoBackend.Data;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Series;

namespace RenzoBackend.Services.Downloads;

/// <summary>
/// Moves waiting chapters off a source whose download queue has stopped moving.
///
/// A step-down only ever happened when a source FAILED, and a job that never
/// starts never fails. So when a source's queue jammed, every chapter waiting on
/// it waited with it, however many other sources had the chapter. On 2026-11-03
/// Comic Asura completed nothing for a whole day, and 70 chapters of "Caught by
/// the Villain" sat behind it, chapters 0-57 among them, while MangaFire had 67
/// of them and was downloading at full speed.
///
/// A source counts as stalled when it has completed no download for an hour
/// AND has had a chapter due and waiting for that long. A long queue that is
/// moving is not stalled: its downloads keep completing. Each of its chapters
/// waiting over an hour moves to the best other source that has the chapter
/// (DownloadSourceSelector.PickHandOff: not failed recently, a free copy before
/// a paid one) and has itself completed a download within the hour, which is
/// the only evidence that a source can take more work: a captcha-gated source's
/// own queue also looks short, because everything on it fails fast. A source
/// that delivers but has a long queue still qualifies: any delivery beats none,
/// and the fair queue gives the moved chapters' series their own turns. Each
/// pass hands a source no more than it completed in the last hour. The stalled
/// source is stepped down for that series (SourceFailureKind.Backlog), so its
/// next chapters go elsewhere too until the usual re-check.
///
/// A chapter the user asked for from this very source ("Re-download from X")
/// is left waiting for it.
///
/// A chapter already on disk from some source is left where it is: that job is
/// an upgrade to a better copy, and moving it would only duplicate a file the
/// series already has.
/// </summary>
public sealed class DownloadRebalanceService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>No completed download, and a chapter due, for this long: the source is stalled.</summary>
    internal static readonly TimeSpan StalledAfter = TimeSpan.FromHours(1);

    private const int MaxMovesPerPass = 1000;

    /// <summary>
    /// Chapters handed to one source per pass: what it completed in the last
    /// hour, within these bounds, so a pass cannot bury it.
    /// </summary>
    private const int MinMovesPerTargetPerPass = 10;
    private const int MaxMovesPerTargetPerPass = 200;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly SourceHealthService _health;
    private readonly ILogger<DownloadRebalanceService> _logger;

    public DownloadRebalanceService(IServiceScopeFactory scopeFactory, SourceHealthService health, ILogger<DownloadRebalanceService> logger)
    {
        _scopeFactory = scopeFactory;
        _health = health;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(StartupDelay, stoppingToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Download rebalance pass failed");
            }

            try { await Task.Delay(Interval, stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private sealed record DueRow(Guid Id, string? GroupKey, DateTime ScheduledDate, string JobParameters);

    internal async Task<int> RunOnceAsync(CancellationToken token)
    {
        using IServiceScope scope = _scopeFactory.CreateScope();
        AppDbContext db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        DateTime now = DateTime.UtcNow;

        List<DueRow> due = await db.Queues.AsNoTracking()
            .Where(q => q.Queue == "Downloads" && q.JobType == JobType.Download
                && q.Status == QueueStatus.Waiting && q.ScheduledDate <= now)
            .Select(q => new DueRow(q.Id, q.GroupKey, q.ScheduledDate, q.JobParameters ?? ""))
            .ToListAsync(token).ConfigureAwait(false);
        if (due.Count == 0)
            return 0;

        Dictionary<string, DateTime> oldestDue = due
            .GroupBy(r => r.GroupKey ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.Min(r => r.ScheduledDate), StringComparer.Ordinal);

        bool Stalled(string group) =>
            now - _health.LastDelivered(group) >= StalledAfter
            && oldestDue.TryGetValue(group, out DateTime o) && now - o >= StalledAfter;
        // Positive evidence only: it delivered within the hour.
        bool KeepingUp(string group) =>
            !Stalled(group) && now - _health.LastDelivered(group) < StalledAfter;
        int Room(string group) =>
            Math.Clamp(_health.DeliveredLastHour(group), MinMovesPerTargetPerPass, MaxMovesPerTargetPerPass);

        // Every stuck row is looked at; the cap is on moves. Capping the rows
        // looked at let a stalled source's oldest rows that cannot move (already
        // on disk, no other source) fill the cap and block every move behind them.
        List<DueRow> stuck = due
            .Where(r => r.GroupKey != null && now - r.ScheduledDate >= StalledAfter && Stalled(r.GroupKey))
            .OrderBy(r => r.ScheduledDate)
            .ToList();
        if (stuck.Count == 0)
            return 0;

        Dictionary<Guid, SeriesEntity?> seriesById = [];
        Dictionary<Guid, HashSet<string>> loginsByOwner = [];
        Dictionary<(Guid Series, string From, string To), int> moves = [];
        Dictionary<string, int> movedTo = new(StringComparer.Ordinal);
        HashSet<Guid> steppedDown = [];
        int moved = 0;

        foreach (DueRow row in stuck)
        {
            if (moved >= MaxMovesPerPass)
                break;
            ChapterDownload? ch;
            try { ch = JsonSerializer.Deserialize<ChapterDownload>(row.JobParameters); }
            catch (JsonException) { continue; }
            if (ch?.Chapter == null)
                continue;
            // The user picked this source; it keeps the chapter (ChapterDownload.ForcedSource).
            if (ch.ForcedSource)
                continue;

            if (!seriesById.TryGetValue(ch.SeriesId, out SeriesEntity? series))
            {
                series = await db.Series.Include(s => s.Sources).AsNoTracking()
                    .FirstOrDefaultAsync(s => s.Id == ch.SeriesId, token).ConfigureAwait(false);
                seriesById[ch.SeriesId] = series;
            }
            if (series == null || series.PauseDownloads)
                continue;

            decimal number = ch.Chapter.ParsedNumber;
            if (series.Sources.Any(sp => sp.Chapters.Any(c => c.Number == number && !c.IsDeleted && !string.IsNullOrEmpty(c.Filename))))
                continue;

            if (!loginsByOwner.TryGetValue(series.OwnerId, out HashSet<string>? logins))
            {
                Guid owner = series.OwnerId;
                logins = (await db.SiteCredentials.AsNoTracking()
                        .Where(c => (c.Status == "ok" || c.Status == "manual_cookie") && (owner == Guid.Empty || c.UserId == owner))
                        .Select(c => c.Provider)
                        .ToListAsync(token).ConfigureAwait(false))
                    .ToHashSet(StringComparer.Ordinal);
                loginsByOwner[owner] = logins;
            }

            string from = row.GroupKey!;
            SeriesProviderEntity? target = DownloadSourceSelector.PickHandOff(series.Sources, number,
                p => p.Id == ch.SeriesProviderId || p.Provider == from || ch.TriedSourceIds.Contains(p.Id)
                    || !KeepingUp(p.Provider) || movedTo.GetValueOrDefault(p.Provider) >= Room(p.Provider),
                _health.IsSteppedDown, allowSteppedDown: false, logins, now);
            if (target == null)
                continue;

            Chapter stored = target.Chapters.First(c => !c.IsDeleted && c.Number == number);
            ChapterDownload fresh = series.ToDownloads(target,
                [SeriesCommandService.ChapterToParsedChapter(stored, target)], series.StoragePath)[0];
            fresh.TriedSourceIds = [.. ch.TriedSourceIds];
            fresh.LeftEarlySourceIds = [.. ch.LeftEarlySourceIds];
            fresh.HandedOff = true;
            string json = JsonSerializer.Serialize(fresh);

            // Only while it is still waiting: the picker may have just taken it.
            int changed = await db.Queues
                .Where(q => q.Id == row.Id && q.Status == QueueStatus.Waiting)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(q => q.JobParameters, json)
                    .SetProperty(q => q.GroupKey, target.Provider)
                    .SetProperty(q => q.ScheduledDate, now)
                    .SetProperty(q => q.RetryCount, 0), token)
                .ConfigureAwait(false);
            if (changed == 0)
                continue;

            moved++;
            movedTo[target.Provider] = movedTo.GetValueOrDefault(target.Provider) + 1;
            var k = (series.Id, from, target.Provider ?? string.Empty);
            moves[k] = moves.GetValueOrDefault(k) + 1;

            SeriesProviderEntity? stalled = series.Sources.FirstOrDefault(p => p.Id == ch.SeriesProviderId);
            if (stalled != null && steppedDown.Add(stalled.Id))
                _health.ReportFailure(stalled, series.Title, SourceFailureKind.Backlog);
        }

        foreach (((Guid seriesId, string from, string to), int n) in moves)
        {
            _logger.LogInformation(
                "Moved {Count} waiting chapter(s) of '{Series}' from {From} to {To}: {From} has completed no download since {LastDelivered:yyyy-MM-dd HH:mm} UTC and its oldest waiting chapter has been due since {OldestDue:yyyy-MM-dd HH:mm} UTC.",
                n, seriesById[seriesId]?.Title, from, to, from, _health.LastDelivered(from), oldestDue.GetValueOrDefault(from));
        }
        return moved;
    }
}
