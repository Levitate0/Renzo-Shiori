using System.Text.Json;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// Re-pointing a library row at its source's new id, in place (Hochiya-san: 39
/// files on a dead Asmodeus id, its only source). The chapter merge must leave
/// every file and download state exactly as it was.
/// </summary>
public class SourceIdRepointTests
{
    private const string OldId = "eu.kanade.tachiyomi.extension.en.valirscans|8440187396173658748";
    private const string NewId = "eu.kanade.tachiyomi.extension.en.valirscans|9114028255622294379";
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    private static readonly HashSet<string> NoLogins = [];

    private static SeriesProviderEntity DeadRow(int priority = 2, decimal? cutoff = null, bool storage = false, params Chapter[] chapters) => new()
    {
        Id = Guid.NewGuid(),
        Provider = "Valir Scans",
        Scanlator = "Valir Scans",
        Title = "The Reason Why That Villainess Picked Up A Sword",
        Language = "en",
        Priority = priority,
        IsStorage = storage,
        ContinueAfterChapter = cutoff,
        IsUninstalled = true,
        MihonProviderId = OldId,
        MihonId = OldId + "|/series/comic/the-reason-why",
        Url = "https://valirscans.org/series/comic/the-reason-why",
        BridgeItemInfo = "{\"url\":\"/series/comic/the-reason-why\"}",
        ConsecutiveErrorCount = 4,
        LastErrorDate = Now.AddDays(-60),
        Chapters = chapters.ToList(),
    };

    private static Chapter Listed(decimal n) => new()
    {
        Number = n,
        Name = $"Chapter {n}",
        Url = $"https://valirscans.org/series/comic/the-reason-why/chapter/{n}",
        ProviderIndex = (int)(n * 10),
        ProviderUploadDate = new DateTime(2026, 1, 1).AddDays((double)n),
    };

    private static ProviderSeriesDetails NewListing(params decimal[] numbers) => new()
    {
        MihonProviderId = NewId,
        MihonId = NewId + "|cmmn7ocd4000cxgqqpem5643z",
        BridgeItemInfo = "{\"url\":\"cmmn7ocd4000cxgqqpem5643z\"}",
        Url = "https://valirscans.org/series/comic/cmmn7ocd4000cxgqqpem5643z",
        Provider = "Valir Scans",
        Scanlator = "Valir Scans",
        Title = "The Reason Why That Villainess Picked Up A Sword",
        Chapters = numbers.Select(n => new Chapter
        {
            Number = n,
            Name = $"Chapter {n} (new)",
            Url = $"https://valirscans.org/new/{n}",
            ProviderIndex = 1000 + (int)(n * 10),
            ProviderUploadDate = new DateTime(2026, 2, 1),
        }).ToList(),
    };

    private static decimal[] Range(int from, int to) => Enumerable.Range(from, to - from + 1).Select(i => (decimal)i).ToArray();

    /// <summary>The fields a re-point must never touch, per chapter, as one comparable string.</summary>
    private static string Kept(Chapter c) => JsonSerializer.Serialize(new
    {
        c.Number, c.Filename, c.DownloadDate, c.PageCount, c.Pages, c.ShouldDownload, c.DownloadDeleted, c.IsDeleted, c.Name,
        c.ProviderUploadDate, c.DateFetched,
    });

    [Fact]
    public void The_merge_keeps_every_file_and_download_state_and_relinks_by_exact_number()
    {
        var withFile = Listed(1m);
        withFile.Filename = "[Valir Scans][en] The Reason Why 01.cbz";
        withFile.DownloadDate = Now.AddDays(-90);
        withFile.PageCount = 20;
        withFile.Pages = ["001.webp", "002.webp"];
        withFile.IsLocked = true;
        withFile.LockedCheckedAt = Now.AddDays(-80);
        withFile.GaveUpAt = Now.AddDays(-70);
        withFile.GiveUpCount = 2;
        var deleted = Listed(2m);
        deleted.DownloadDeleted = true;
        deleted.IsLocked = true;
        deleted.LockedCheckedAt = Now.AddDays(-80);
        var dedupCopy = Listed(2.5m);
        dedupCopy.IsDeleted = true;
        var wanted = Listed(3m);
        wanted.ShouldDownload = true;
        var fraction = Listed(22.2m);
        var gone = Listed(99m);
        gone.Filename = "[Valir Scans][en] The Reason Why 99.cbz";
        SeriesProviderEntity row = DeadRow(priority: 2, cutoff: 4m, storage: true, withFile, deleted, dedupCopy, wanted, fraction, gone);
        row.IsTitle = row.IsCover = row.IsStatus = true;
        Guid id = row.Id;
        Dictionary<decimal, string> before = row.Chapters.ToDictionary(c => c.Number!.Value, Kept);

        // 22.2 routed through a float is 22.200000762939453: a different chapter.
        decimal viaFloat = (decimal)(double)22.2f;
        ProviderSeriesDetails listing = NewListing(1m, 2m, 2.5m, 3m, 4m, 5m, viaFloat);
        RepointCounts counts = SourceMigrationService.ApplyRepoint(row, listing, "https://valirscans.org/cover.webp", Now);

        Assert.Equal(new RepointCounts(Matched: 4, Added: 3, Unmatched: 2, UnmatchedFiles: 1), counts);
        foreach ((decimal n, string kept) in before)
            Assert.Equal(kept, Kept(row.Chapters.First(c => c.Number == n)));

        foreach (decimal n in new[] { 1m, 2m, 2.5m, 3m })
        {
            Chapter c = row.Chapters.First(x => x.Number == n);
            Chapter nc = listing.Chapters.First(x => x.Number == n);
            Assert.Equal((nc.Url, nc.ProviderIndex), (c.Url, c.ProviderIndex));
            Assert.Equal((null, null, 0), (c.LockedCheckedAt, c.GaveUpAt, c.GiveUpCount));
        }
        Assert.True(row.Chapters.First(c => c.Number == 1m).IsLocked);   // has its file: left as it is
        Assert.False(row.Chapters.First(c => c.Number == 2m).IsLocked);  // the new site's scan decides
        // Unmatched: kept, with the old link.
        Assert.Equal("https://valirscans.org/series/comic/the-reason-why/chapter/22.2", row.Chapters.First(c => c.Number == 22.2m).Url);
        Assert.Equal("https://valirscans.org/series/comic/the-reason-why/chapter/99", row.Chapters.First(c => c.Number == 99m).Url);

        // New numbers, as a scan of an established row adds them: within the cutoff (4).
        var added = row.Chapters.Where(c => !before.ContainsKey(c.Number!.Value)).ToList();
        Assert.Equal([4m, 5m, viaFloat], added.Select(c => c.Number!.Value));
        Assert.Equal([false, true, true], added.Select(c => c.ShouldDownload));
        Assert.All(added, c => Assert.Equal(Now, c.DateFetched));
        Assert.All(added, c => Assert.Null(c.Filename));
        Assert.Equal(9, row.ChapterCount);

        // The row: same id and place, now on the new id and installed.
        Assert.Equal(id, row.Id);
        Assert.Equal((2, true, true, true, true, false, 4m), (row.Priority, row.IsStorage, row.IsTitle, row.IsCover, row.IsStatus, row.IsDisabled, row.ContinueAfterChapter!.Value));
        Assert.Equal(("The Reason Why That Villainess Picked Up A Sword", "Valir Scans", "Valir Scans"), (row.Title, row.Provider, row.Scanlator));
        Assert.Equal((NewId, listing.MihonId, listing.BridgeItemInfo, listing.Url), (row.MihonProviderId, row.MihonId, row.BridgeItemInfo, row.Url));
        Assert.Equal("https://valirscans.org/cover.webp", row.ThumbnailUrl);
        Assert.Equal((false, false, 0, null), (row.IsUninstalled, row.IsLocal, row.ConsecutiveErrorCount, row.LastErrorDate));
    }

    [Fact]
    public void Numbers_are_matched_as_decimals()
    {
        SeriesProviderEntity row = DeadRow(chapters: Listed(1.0m));
        RepointCounts counts = SourceMigrationService.ApplyRepoint(row, NewListing(1m), null, Now);
        Assert.Equal((1, 0), (counts.Matched, counts.Added));
        Assert.Null(row.ThumbnailUrl);
    }

    [Fact]
    public void A_switched_off_row_is_re_pointed_and_stays_off()
    {
        SeriesProviderEntity row = DeadRow(chapters: Listed(1m));
        row.IsDisabled = true;
        SourceMigrationService.ApplyRepoint(row, NewListing(1m, 2m), null, Now);
        Assert.True(row.IsDisabled);
        var series = new SeriesEntity { Title = "X", Sources = [row] };
        Assert.Equal(0, SourceMigrationService.FirstScanDownloads(series, row, NewListing(1m, 2m), downloadAll: true, allowLocked: false));
        Assert.Equal(0, SourceMigrationService.UpgradeExposure(series, row, NoLogins));
    }

    [Fact]
    public void A_re_pointed_storage_row_downloads_only_what_it_never_had()
    {
        // Hochiya-san: storage, "download all" on, 39 files, the new id lists 1–44.
        Chapter[] held = Range(1, 39).Select(n =>
        {
            Chapter c = Listed(n);
            c.Filename = $"[Asmodeus Scans][en] Hochiya-san is a Handful {n:00}.cbz";
            return c;
        }).ToArray();
        SeriesProviderEntity row = DeadRow(priority: 0, cutoff: 39m, storage: true, held);
        var series = new SeriesEntity { Title = "Hochiya-san is a Handful", Sources = [row] };
        ProviderSeriesDetails listing = NewListing(Range(1, 44));
        SourceMigrationService.ApplyRepoint(row, listing, null, Now);

        var queued = series.GenerateDownloadsFromChapterData(row,
            listing.Chapters.Select(c => SeriesCommandService.ChapterToParsedChapter(c, row)).ToList(), downloadAll: true);
        Assert.Equal(Range(40, 44), queued.Select(d => d.Chapter.ParsedNumber).OrderBy(n => n));
        Assert.Equal(5, SourceMigrationService.FirstScanDownloads(series, row, listing, downloadAll: true, allowLocked: false));
        Assert.Equal(0, SourceMigrationService.UpgradeExposure(series, row, NoLogins));
        Assert.Equal(39, row.Chapters.Count(c => c.Filename != null));
    }

    [Fact]
    public void The_measured_listing_is_known_before_the_first_scan()
    {
        // The scan queues priority upgrades only for chapters it creates itself
        // (newly gained): without the merge, 74–83 would be new to it and queue
        // against MangaFire's files right away.
        SeriesProviderEntity row = DeadRow(priority: 2, chapters: Range(1, 73).Select(Listed).ToArray());
        RepointCounts counts = SourceMigrationService.ApplyRepoint(row, NewListing(Range(1, 83)), null, Now);
        Assert.Equal(10, counts.Added);
        Assert.All(Range(1, 83), n => Assert.Contains(row.Chapters, c => c.Number == n));
    }

    [Fact]
    public void The_upgrade_exposure_follows_the_priority_upgrade_rule()
    {
        // The Reason Why: Valir (2) back on, MangaFire (3, switched off but not
        // uninstalled, so its files count) holds 1–70; a better source (0) lists
        // 1–10 and takes those itself.
        SeriesProviderEntity valir = DeadRow(priority: 2, chapters: Range(1, 73).Select(Listed).ToArray());
        SourceMigrationService.ApplyRepoint(valir, NewListing(Range(1, 83)), null, Now);
        SeriesProviderEntity mangaFire = new()
        {
            Id = Guid.NewGuid(), Provider = "MangaFire", Priority = 3, IsDisabled = true, MihonProviderId = "mangafire|1",
            Chapters = Range(1, 70).Select(n => { Chapter c = Listed(n); c.Filename = $"mf {n}.cbz"; return c; }).ToList(),
        };
        SeriesProviderEntity better = new()
        {
            Id = Guid.NewGuid(), Provider = "Asura", Priority = 0, MihonProviderId = "asura|1",
            Chapters = Range(1, 10).Select(Listed).ToList(),
        };
        var series = new SeriesEntity { Title = "X", Sources = [better, valir, mangaFire] };
        Assert.Equal(60, SourceMigrationService.UpgradeExposure(series, valir, NoLogins));

        // Paid there and no login for it: those fall to Valir. With a login they stay with it.
        better.Chapters.ForEach(c => c.IsLocked = true);
        Assert.Equal(70, SourceMigrationService.UpgradeExposure(series, valir, NoLogins));
        Assert.Equal(60, SourceMigrationService.UpgradeExposure(series, valir, new HashSet<string> { "Asura" }));

        // Still uninstalled (never followed): not a candidate at all.
        valir.IsUninstalled = true;
        Assert.Equal(0, SourceMigrationService.UpgradeExposure(series, valir, NoLogins));
    }
}
