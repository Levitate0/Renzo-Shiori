using Microsoft.Extensions.Logging.Abstractions;
using ParsedChapter = Mihon.ExtensionsBridge.Models.Extensions.ParsedChapter;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Background;
using RenzoBackend.Services.Downloads;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// Stepping a failing source down and re-checking it later (2026-11-03: AllManga's
/// captcha tried 832 times in a day for one series, and one failing series holding
/// Comic Asura's queue for a day).
/// </summary>
public class SourceStepDownTests
{
    private DateTime _now = new(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>Started 20 minutes ago, past the startup grace, unless a test says otherwise.</summary>
    private SourceHealthService Health()
    {
        _now = _now.AddMinutes(-20);
        var health = new SourceHealthService(NullLogger<SourceHealthService>.Instance, () => _now);
        _now = _now.AddMinutes(20);
        return health;
    }

    private static SeriesProviderEntity Source(string name, int priority, string site, bool storage = false, params decimal[] chapters) => new()
    {
        Id = Guid.NewGuid(),
        Provider = name,
        Priority = priority,
        IsStorage = storage,
        MihonProviderId = site,
        Chapters = chapters.Select(n => new Chapter { Number = n }).ToList(),
    };

    [Fact]
    public void A_captcha_steps_the_source_down_for_every_series()
    {
        var health = Health();
        var a = Source("AllManga", 0, "allmanga|1");
        var sameSiteOtherSeries = Source("AllManga", 0, "allmanga|1");

        Assert.True(health.ReportFailure(a, "Caught by the Villain", SourceFailureKind.Captcha));
        Assert.True(health.IsSteppedDown(a));
        Assert.True(health.IsSteppedDown(sameSiteOtherSeries));
    }

    [Fact]
    public void One_error_is_not_enough_and_a_success_starts_over()
    {
        var health = Health();
        var a = Source("MangaFire", 0, "mangafire|1");

        Assert.False(health.ReportFailure(a, "s", SourceFailureKind.Error));
        health.ReportSuccess(a, "s");
        Assert.False(health.ReportFailure(a, "s", SourceFailureKind.Error));
        Assert.True(health.ReportFailure(a, "s", SourceFailureKind.Error));
        Assert.True(health.IsSteppedDown(a));
    }

    [Fact]
    public void Our_own_restarts_never_step_a_source_down()
    {
        var health = Health();
        var a = Source("MangaFire", 0, "mangafire|1");
        for (int i = 0; i < 5; i++)
            health.ReportFailure(a, "s", SourceFailureKind.Transport);
        Assert.False(health.IsSteppedDown(a));
    }

    [Fact]
    public void It_is_rechecked_after_the_wait_and_a_failed_recheck_waits_twice_as_long()
    {
        var health = Health();
        var a = Source("Comic Asura", 0, "asura|1");

        health.ReportFailure(a, "s", SourceFailureKind.Timeout);
        _now = _now.AddMinutes(29);
        Assert.True(health.IsSteppedDown(a));
        _now = _now.AddMinutes(2);
        Assert.False(health.IsSteppedDown(a)); // re-check time: ranked normally again

        // The re-check fails with a plain error: straight back down, for an hour.
        Assert.True(health.ReportFailure(a, "s", SourceFailureKind.Error));
        _now = _now.AddMinutes(59);
        Assert.True(health.IsSteppedDown(a));
        _now = _now.AddMinutes(2);
        Assert.False(health.IsSteppedDown(a));

        Assert.Equal(TimeSpan.FromHours(24), SourceHealthService.StepDownFor(20));
    }

    [Fact]
    public void Failures_already_stepped_down_do_not_stretch_the_wait()
    {
        var health = Health();
        var a = Source("AllManga", 0, "allmanga|1");
        health.ReportFailure(a, "s", SourceFailureKind.Captcha);
        for (int i = 0; i < 10; i++)
            health.ReportFailure(a, "s", SourceFailureKind.Captcha); // jobs queued before the step-down
        _now = _now.AddMinutes(31);
        Assert.False(health.IsSteppedDown(a));
    }

    [Fact]
    public void Exceptions_are_classified()
    {
        Assert.Equal(SourceFailureKind.Timeout, SourceHealthService.Classify(new TimeoutException()));
        Assert.Equal(SourceFailureKind.Captcha, SourceHealthService.Classify(new Exception("/source/pages failed: Solve captcha in WebView and retry")));
        Assert.Equal(SourceFailureKind.Transport, SourceHealthService.Classify(new HttpRequestException("The response ended prematurely")));
        Assert.Equal(SourceFailureKind.Transport, SourceHealthService.Classify(new Exception("/source/pages failed: Source 123 not loaded")));
        Assert.Equal(SourceFailureKind.Error, SourceHealthService.Classify(new Exception("/source/pages failed: HTTP error 500")));
    }

    [Fact]
    public void A_stepped_down_top_source_stands_aside_for_the_next_one()
    {
        var health = Health();
        var allManga = Source("AllManga", 0, "allmanga|1", storage: true, 1, 2, 3);
        var mangaFire = Source("MangaFire", 1, "mangafire|1", storage: false, 1, 2, 3);
        var series = new SeriesEntity { Sources = [allManga, mangaFire] };

        // Healthy: the storage source keeps everything, MangaFire nothing.
        Assert.True(DownloadSourceSelector.Prepare(series, allManga, steppedDown: health.IsSteppedDown).Keeps(2));
        Assert.False(DownloadSourceSelector.Prepare(series, mangaFire, steppedDown: health.IsSteppedDown).Keeps(2));

        health.ReportFailure(allManga, "s", SourceFailureKind.Captcha);

        Assert.False(DownloadSourceSelector.Prepare(series, allManga, steppedDown: health.IsSteppedDown).Keeps(2));
        Assert.True(DownloadSourceSelector.Prepare(series, mangaFire, steppedDown: health.IsSteppedDown).Keeps(2));
    }

    [Fact]
    public void A_stepped_down_source_is_still_the_last_resort()
    {
        var health = Health();
        var allManga = Source("AllManga", 0, "allmanga|1", storage: false, 1, 2, 199);
        var mangaFire = Source("MangaFire", 1, "mangafire|1", storage: false, 1, 2);
        var series = new SeriesEntity { Sources = [allManga, mangaFire] };
        health.ReportFailure(allManga, "s", SourceFailureKind.Captcha);

        // Only AllManga lists 199, so it still gets it.
        Assert.True(DownloadSourceSelector.Prepare(series, allManga, steppedDown: health.IsSteppedDown).Keeps(199));
    }

    private static EnqueueEntity Job(string series, decimal chapter, int retries = 0) => new()
    {
        JobType = Models.Enums.JobType.Download,
        Key = $"Download_{series}|{chapter}",
        ExtraKey = series,
        RetryCount = retries,
    };

    [Fact]
    public void A_failing_series_cannot_hold_its_sources_slots()
    {
        // Baby Cat's lowest chapters fail and retry; it also has the oldest job.
        var jobs = new List<EnqueueEntity>
        {
            Job("babycat", 0, retries: 6), Job("babycat", 1, retries: 6), Job("babycat", 36),
            Job("villain", 0), Job("villain", 1), Job("other", 5),
        };

        List<string> firstTwo = JobQueueHostedService.SeriesInReadingOrder(jobs)
            .Take(2).Select(j => j.Key).ToList();

        // One turn for Baby Cat, spent on the chapter it has not tried yet.
        Assert.Equal(["Download_babycat|36", "Download_villain|0"], firstTwo);
    }

    [Fact]
    public void A_series_with_only_retries_still_gets_its_turn()
    {
        var jobs = new List<EnqueueEntity> { Job("a", 1), Job("a", 2), Job("b", 4, retries: 2), Job("c", 1), Job("c", 2) };
        List<string> round = JobQueueHostedService.SeriesInReadingOrder(jobs).Take(3).Select(j => j.Key).ToList();
        Assert.Contains("Download_b|4", round);
    }

    [Fact]
    public void Each_series_still_goes_lowest_chapter_first_and_the_series_take_turns()
    {
        var jobs = new List<EnqueueEntity> { Job("a", 3), Job("a", 1), Job("a", 2), Job("b", 9), Job("b", 7) };

        List<string> order = JobQueueHostedService.SeriesInReadingOrder(jobs).Select(j => j.Key).ToList();
        Assert.Equal(["Download_a|1", "Download_b|7", "Download_a|2", "Download_b|9", "Download_a|3"], order);

        List<string> rotated = JobQueueHostedService.SeriesInReadingOrder(jobs, rotation: 1).Select(j => j.Key).ToList();
        Assert.Equal("Download_b|7", rotated[0]);
    }

    [Fact]
    public void A_chapter_given_up_on_waits_out_its_backoff()
    {
        var src = Source("Comic Asura", 0, "asura|1", storage: false, 13, 14);
        src.Chapters[0].GaveUpAt = DateTime.UtcNow.AddHours(-1);
        src.Chapters[0].GiveUpCount = 1;
        var series = new SeriesEntity { Sources = [src], StoragePath = "x" };
        var listing = new List<ParsedChapter>
        {
            new() { ParsedNumber = 13, Name = "Chapter 13", ParsedName = "Chapter 13" },
            new() { ParsedNumber = 14, Name = "Chapter 14", ParsedName = "Chapter 14" },
        };

        List<decimal> queued = series.GenerateDownloadsFromChapterData(src, listing, downloadAll: true)
            .Select(d => d.Chapter.ParsedNumber).ToList();
        Assert.Equal([14m], queued);

        src.Chapters[0].GaveUpAt = DateTime.UtcNow.AddHours(-7);
        queued = series.GenerateDownloadsFromChapterData(src, listing, downloadAll: true)
            .Select(d => d.Chapter.ParsedNumber).ToList();
        Assert.Equal([13m, 14m], queued);

        Assert.Equal(TimeSpan.FromHours(12), DownloadsExtensions.GiveUpBackoff(2));
        Assert.Equal(TimeSpan.FromDays(7), DownloadsExtensions.GiveUpBackoff(30));
    }

    [Fact]
    public void The_same_chapter_failing_again_is_not_a_second_error()
    {
        var health = Health();
        var a = Source("MangaFire", 0, "mangafire|1");
        Assert.False(health.ReportFailure(a, "s", SourceFailureKind.Error, chapter: 7));
        Assert.False(health.ReportFailure(a, "s", SourceFailureKind.Error, chapter: 7));
        Assert.False(health.IsSteppedDown(a));
        Assert.True(health.ReportFailure(a, "s", SourceFailureKind.Error, chapter: 8));
    }

    [Fact]
    public void Our_own_solver_being_down_is_not_the_sites_fault()
    {
        Assert.Equal(SourceFailureKind.Transport, SourceHealthService.Classify(
            new Exception("image failed: {java.io.IOException: Failed to connect to /127.0.0.1:8191}")));
        Assert.Equal(SourceFailureKind.Transport, SourceHealthService.Classify(
            new Exception("unexpected end of stream on http://127.0.0.1:8191/...")));
    }

    private static readonly IReadOnlySet<string> NoLogins = new HashSet<string>();

    [Fact]
    public void A_hand_off_never_goes_back_to_a_source_the_chapter_failed_on()
    {
        var a = Source("A", 0, "a|1", storage: false, 5);
        var b = Source("B", 1, "b|1", storage: false, 5);
        var tried = new HashSet<Guid> { a.Id };

        // Failed on A, now failing on B: nowhere left, so it is given up on.
        Assert.Null(DownloadSourceSelector.PickHandOff([a, b], 5, p => p.Id == b.Id || tried.Contains(p.Id),
            null, allowSteppedDown: true, NoLogins, _now));
        // Without the record it would bounce back to A.
        Assert.Same(a, DownloadSourceSelector.PickHandOff([a, b], 5, p => p.Id == b.Id,
            null, allowSteppedDown: true, NoLogins, _now));
    }

    [Fact]
    public void A_hand_off_prefers_a_better_source_above_the_failed_one()
    {
        var top = Source("Top", 0, "t|1", storage: false, 5);
        var failed = Source("Mid", 1, "m|1", storage: false, 5);
        var low = Source("Low", 2, "l|1", storage: false, 5);
        Assert.Same(top, DownloadSourceSelector.PickHandOff([top, failed, low], 5, p => p.Id == failed.Id,
            null, allowSteppedDown: false, NoLogins, _now));
    }

    [Fact]
    public void A_hand_off_skips_a_paid_copy_unless_there_is_a_login()
    {
        var failed = Source("Free", 0, "f|1", storage: false, 5);
        var paid = Source("Violet", 1, "v|1", storage: false, 5);
        paid.Chapters[0].IsLocked = true;
        var low = Source("MangaFire", 2, "mf|1", storage: false, 5);

        Assert.Same(low, DownloadSourceSelector.PickHandOff([failed, paid, low], 5, p => p.Id == failed.Id,
            null, false, NoLogins, _now));
        // With a login, a free copy still comes first...
        Assert.Same(low, DownloadSourceSelector.PickHandOff([failed, paid, low], 5, p => p.Id == failed.Id,
            null, false, new HashSet<string> { "Violet" }, _now));
        // ...and the paid one is used when it is the only other copy.
        Assert.Same(paid, DownloadSourceSelector.PickHandOff([failed, paid], 5, p => p.Id == failed.Id,
            null, false, new HashSet<string> { "Violet" }, _now));
        // Unless it was found paid there recently.
        paid.Chapters[0].LockedCheckedAt = _now.AddHours(-1);
        Assert.Null(DownloadSourceSelector.PickHandOff([failed, paid], 5, p => p.Id == failed.Id,
            null, false, new HashSet<string> { "Violet" }, _now));
    }

    [Fact]
    public void An_early_hand_off_waits_rather_than_use_a_stepped_down_source()
    {
        var health = Health();
        var failed = Source("A", 0, "a|1", storage: false, 5);
        var down = Source("B", 1, "b|1", storage: false, 5);
        health.ReportFailure(down, "s", SourceFailureKind.Captcha);

        Assert.Null(DownloadSourceSelector.PickHandOff([failed, down], 5, p => p.Id == failed.Id,
            health.IsSteppedDown, allowSteppedDown: false, NoLogins, _now));
        Assert.Same(down, DownloadSourceSelector.PickHandOff([failed, down], 5, p => p.Id == failed.Id,
            health.IsSteppedDown, allowSteppedDown: true, NoLogins, _now));
    }

    [Fact]
    public void Download_all_queues_given_up_chapters_anyway()
    {
        var src = Source("Comic Asura", 0, "asura|1", storage: false, 13);
        src.Chapters[0].GaveUpAt = DateTime.UtcNow;
        src.Chapters[0].GiveUpCount = 3;
        var series = new SeriesEntity { Sources = [src], StoragePath = "x" };
        var listing = new List<ParsedChapter> { new() { ParsedNumber = 13, Name = "Chapter 13", ParsedName = "Chapter 13" } };
        Assert.Empty(series.GenerateDownloadsFromChapterData(src, listing, downloadAll: true));
        Assert.Single(series.GenerateDownloadsFromChapterData(src, listing, downloadAll: true, ignoreGiveUp: true));
    }

    [Fact]
    public void A_scan_keeps_what_downloads_wrote_meanwhile()
    {
        // What the scan loaded, what it made of it, and what is stored by the time it saves.
        var original = new List<Chapter> { new() { Number = 1 }, new() { Number = 2, Name = "Chapter 2 \U0001F512", IsLocked = true }, new() { Number = 3 } };
        var working = new List<Chapter>
        {
            new() { Number = 1 },
            new() { Number = 2, Name = "Chapter 2", IsLocked = false, ShouldDownload = true }, // the scan unlocked it
            new() { Number = 3 },
            new() { Number = 4, Name = "Chapter 4" },                                        // the scan found it
        };
        var stored = new List<Chapter>
        {
            new() { Number = 1, Filename = "c1.cbz", PageCount = 20, DownloadDate = _now },   // downloaded meanwhile
            new() { Number = 2, Name = "Chapter 2 \U0001F512", IsLocked = true },
            new() { Number = 3, IsLocked = true, LockedCheckedAt = _now, GaveUpAt = _now, GiveUpCount = 2 },
            new() { Number = 5, Filename = "c5.cbz" },                                         // added by a download meanwhile
        };

        List<Chapter> merged = Services.Series.SeriesCommandService.MergeScan(working, original, stored);

        Assert.Equal("c1.cbz", merged[0].Filename);
        Assert.Equal(20, merged[0].PageCount);
        Assert.False(merged[1].IsLocked);                 // the scan's own change
        Assert.Equal("Chapter 2", merged[1].Name);
        Assert.True(merged[1].ShouldDownload);
        Assert.True(merged[2].IsLocked);                  // a paywall hit meanwhile survives
        Assert.Equal(2, merged[2].GiveUpCount);
        Assert.Equal("Chapter 4", merged.Single(c => c.Number == 4).Name);
        Assert.Equal("c5.cbz", merged.Single(c => c.Number == 5).Filename);
    }

    [Fact]
    public void The_chapter_list_comparer_covers_every_chapter_field()
    {
        // A field the comparer misses is invisible to change tracking: it once lost file names.
        var comparer = Data.ChapterListComparer.Instance;
        foreach (var prop in typeof(Chapter).GetProperties().Where(p => p.CanRead && p.CanWrite && p.Name != nameof(Chapter.Number)))
        {
            var a = new List<Chapter> { new() };
            var b = comparer.Snapshot(a);
            object? value = prop.PropertyType == typeof(string) ? "x"
                : prop.PropertyType == typeof(int) || prop.PropertyType == typeof(int?) ? 7
                : prop.PropertyType == typeof(bool) ? true
                : prop.PropertyType == typeof(decimal?) || prop.PropertyType == typeof(decimal) ? 3m
                : prop.PropertyType == typeof(DateTime?) || prop.PropertyType == typeof(DateTime) ? _now
                : prop.PropertyType == typeof(List<string>) ? new List<string> { "p" }
                : null;
            if (value == null)
                continue;
            prop.SetValue(b[0], value);
            Assert.False(comparer.Equals(a, b), $"ChapterListComparer ignores Chapter.{prop.Name}");
            Assert.Equal(value, prop.GetValue(comparer.Snapshot(b)[0]));
        }
    }

    [Fact]
    public async Task The_keyed_lock_holds_under_contention()
    {
        var keyed = new Utils.KeyedAsyncLock();
        int inside = 0, maxInside = 0;
        await Task.WhenAll(Enumerable.Range(0, 200).Select(async _ =>
        {
            using (await keyed.LockAsync("series"))
            {
                int n = Interlocked.Increment(ref inside);
                lock (keyed) maxInside = Math.Max(maxInside, n);
                await Task.Yield();
                Interlocked.Decrement(ref inside);
            }
        }));
        Assert.Equal(1, maxInside);
    }

    [Fact]
    public void A_stalled_queue_steps_down_downloads_but_not_reading()
    {
        var health = Health();
        var a = Source("Comic Asura", 0, "asura|1");
        health.ReportFailure(a, "s", SourceFailureKind.Backlog);

        Assert.True(health.IsSteppedDown(a));
        Assert.False(health.IsUnhealthy(a));

        health.ReportSuccess(a, "s");                    // a read: the queue is still stalled
        Assert.True(health.IsSteppedDown(a));
        health.ReportSuccess(a, "s", downloaded: true);  // a download completed: it moves again
        Assert.False(health.IsSteppedDown(a));
    }

    [Fact]
    public void Deliveries_are_counted_over_the_last_hour()
    {
        var health = Health();
        health.NoteDelivered("MangaFire");
        _now = _now.AddMinutes(30);
        health.NoteDelivered("MangaFire");
        Assert.Equal(2, health.DeliveredLastHour("MangaFire"));
        _now = _now.AddMinutes(45);
        Assert.Equal(1, health.DeliveredLastHour("MangaFire"));
        Assert.Equal(0, health.DeliveredLastHour("Comic Asura"));
    }

    [Fact]
    public async Task The_job_lock_is_not_handed_out_on_a_cancelled_wait()
    {
        var gate = new Utils.AsyncLock();
        using (await gate.LockAsync())
        {
            using var cts = new CancellationTokenSource();
            Task<IDisposable> waiting = gate.LockAsync(cts.Token);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.LockAsync(cts.Token));
        }
        // Still exactly one holder at a time afterwards.
        IDisposable held = await gate.LockAsync();
        Task<IDisposable> second = gate.LockAsync();
        await Task.Delay(100);
        Assert.False(second.IsCompleted);
        held.Dispose();
        (await second).Dispose();
    }

    [Fact]
    public void Any_delivery_from_a_stalled_queue_ends_its_step_down_and_the_escalation()
    {
        var health = Health();
        var a = Source("Comic Asura", 0, "asura|1");
        health.ReportFailure(a, "s", SourceFailureKind.Backlog);
        Assert.True(health.IsSteppedDown(a));

        _now = _now.AddMinutes(5);
        health.NoteDelivered("Comic Asura");      // another series' chapter came through
        Assert.False(health.IsSteppedDown(a));

        _now = _now.AddHours(3);
        health.ReportFailure(a, "s", SourceFailureKind.Backlog);  // a new stall, not a second in a row
        _now = _now.AddMinutes(31);
        Assert.False(health.IsSteppedDown(a));     // 30 minutes again, not an hour
    }

    [Fact]
    public void A_timeout_right_after_a_start_is_only_an_error()
    {
        var health = new SourceHealthService(NullLogger<SourceHealthService>.Instance, () => _now);
        var a = Source("MangaFire", 0, "mangafire|1");
        Assert.False(health.ReportFailure(a, "s", SourceFailureKind.Timeout, chapter: 155));
        _now = _now.AddMinutes(11);
        Assert.True(health.ReportFailure(a, "s", SourceFailureKind.Timeout, chapter: 156));
    }
}
