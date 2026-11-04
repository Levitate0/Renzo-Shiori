using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// The saved chapter list (2026-11-04): a number stays while any attached source
/// lists it, whatever state the source or row is in, and both the series page and
/// the reader list the same numbers.
/// </summary>
public class ChapterListTests
{
    private static SeriesProviderEntity Source(string name, int priority, params Chapter[] chapters) => new()
    {
        Id = Guid.NewGuid(),
        Provider = name,
        Priority = priority,
        MihonProviderId = name.ToLowerInvariant() + "|1",
        Chapters = [.. chapters],
    };

    private static Chapter Ch(decimal n, string? file = null, bool deleted = false, bool want = true) =>
        new() { Number = n, Name = $"Chapter {n}", Filename = file, IsDeleted = deleted, ShouldDownload = want };

    private static List<decimal> Listed(SeriesEntity s) => s.ListedChapterGroups().Select(g => g.Key).ToList();

    [Fact]
    public void A_chapter_only_a_disabled_source_lists_stays_listed()
    {
        var down = Source("MangaK", 1, Ch(1, "a 1.cbz"), Ch(2));
        down.IsDisabled = true;
        var s = new SeriesEntity { Sources = [Source("MangaFire", 0, Ch(3)), down] };
        Assert.Equal([1m, 2m, 3m], Listed(s));
    }

    [Fact]
    public void A_chapter_whose_rows_are_all_flagged_deleted_stays_listed()
    {
        // Dedup losers and cleaned-up archives: the source still has the chapter.
        var s = new SeriesEntity { Sources = [Source("Philia", 0, Ch(22, "x 22.cbz"), Ch(23, file: "", deleted: true), Ch(24, deleted: true, want: false))] };
        Assert.Equal([22m, 23m, 24m], Listed(s));
    }

    [Fact]
    public void Back_catalogue_below_the_collected_range_stays_hidden()
    {
        var s = new SeriesEntity { Sources = [Source("A", 0, Ch(1, want: false), Ch(2, want: false), Ch(50, "a 50.cbz"), Ch(51))] };
        Assert.Equal([50m, 51m], Listed(s));
    }

    [Fact]
    public void Hidden_decimals_stay_hidden() =>
        Assert.Equal([1m, 2m], Listed(new SeriesEntity { HideDecimalChapters = true, Sources = [Source("A", 0, Ch(1), Ch(1.5m), Ch(2))] }));

    [Fact]
    public void The_series_page_reports_missing_chapters_after_their_neighbour()
    {
        var s = new SeriesEntity { Sources = [Source("A", 0, Ch(27), Ch(28), Ch(29), Ch(31), Ch(32))] };
        var list = s.ToChapterDetailList();
        var at29 = list.Single(c => c.Number == 29);
        Assert.NotNull(at29.MissingAfter);
        Assert.Equal(30m, at29.MissingAfter![0].From);
        Assert.Equal(1, at29.MissingAfter[0].Count);
        Assert.All(list.Where(c => c.Number != 29), c => Assert.Null(c.MissingAfter));
    }

    [Fact]
    public void Locked_is_judged_the_same_way_for_both_lists()
    {
        var paid = Ch(5); paid.IsLocked = true;
        var free = Ch(5);
        var disabled = Source("Free site", 1, free);
        disabled.IsDisabled = true;
        // A free copy only on a disabled source does not make the chapter free.
        var rows = new SeriesEntity { Sources = [Source("Coins", 0, paid), disabled] }.ListedChapterGroups().Single().ToList();
        Assert.True(rows.IsChapterLocked(downloaded: false));
        Assert.False(rows.IsChapterLocked(downloaded: true));
    }

    [Fact]
    public void Our_archive_names_give_back_their_source_and_chapter()
    {
        Assert.True(SeriesArchiveService.TryParseOwnArchiveName("[MangaFire-unofficial][en] Caught by the Villain 058.cbz", out string tag, out decimal n));
        Assert.Equal("MangaFire-unofficial", tag);
        Assert.Equal(58m, n);
        Assert.True(SeriesArchiveService.TryParseOwnArchiveName("[Comic Asura][en] Some Title (The Big Day) 12.5.cbz", out tag, out n));
        Assert.Equal(("Comic Asura", 12.5m), (tag, n));
        Assert.False(SeriesArchiveService.TryParseOwnArchiveName("Some Title v01.cbz", out _, out _));

        var p = new SeriesProviderEntity { Provider = "MangaFire", Scanlator = "unofficial" };
        Assert.Equal("MangaFire-unofficial", SeriesArchiveService.FileTag(p));
        // The tag is what MakeFileNameSafe writes.
        Assert.StartsWith("[" + SeriesArchiveService.FileTag(p) + "]", ArchiveHelperService.MakeFileNameSafe(p.Provider, p.Scanlator, "T", "en", 1m));
    }

    [Fact]
    public void A_series_whose_rows_are_all_flagged_still_lists_them()
    {
        // Kurasu Tensei Tan: 12 rows, all flagged deleted and wanted, no files.
        var s = new SeriesEntity { Sources = [Source("MangaFire", 0, Ch(1, deleted: true), Ch(2, deleted: true), Ch(3, deleted: true))] };
        Assert.Equal([1m, 2m, 3m], Listed(s));
    }

    [Fact]
    public void Deleting_downloads_does_not_take_chapters_off_the_list()
    {
        var gone = Ch(1, want: false); gone.DownloadDeleted = true;
        var gone2 = Ch(2, want: false); gone2.DownloadDeleted = true;
        var s = new SeriesEntity { Sources = [Source("A", 0, gone, gone2, Ch(3, "a 3.cbz", want: false))] };
        Assert.Equal([1m, 2m, 3m], Listed(s));
    }

    [Fact]
    public void Hidden_decimals_are_not_reported_missing()
    {
        // World's Finest Assassin with "hide decimal chapters": 36, 37.1, 37.2, 38.1, 38.2, 39.
        var s = new SeriesEntity { HideDecimalChapters = true, Sources = [Source("A", 0, Ch(36), Ch(37.1m), Ch(37.2m), Ch(38.1m), Ch(38.2m), Ch(39), Ch(41))] };
        var list = s.ToChapterDetailList();
        Assert.Equal([41m, 39m, 36m], list.Select(c => c.Number!.Value));
        Assert.Null(list.Single(c => c.Number == 36).MissingAfter);
        Assert.Equal(40m, list.Single(c => c.Number == 39).MissingAfter!.Single().From);
    }

    [Fact]
    public void A_misnumbered_chapter_zero_does_not_make_the_start_missing()
    {
        var zero = Ch(0, "a 0.cbz"); zero.Name = "Vol.12 Ch.99";
        var s = new SeriesEntity { Sources = [Source("MangaDex", 0, zero, Ch(99, "a 99.cbz"), Ch(100), Ch(101))] };
        Assert.All(s.ToChapterDetailList(), c => Assert.Null(c.MissingAfter));

        var prologue = Ch(0, "b 0.cbz"); prologue.Name = "Prologue";
        var real = new SeriesEntity { Sources = [Source("A", 0, prologue, Ch(50, "b 50.cbz"), Ch(51))] };
        Assert.Equal(1m, real.ToChapterDetailList().Single(c => c.Number == 0).MissingAfter!.Single().From);
    }

    [Fact]
    public void A_leftover_archive_list_does_not_forget_other_downloads()
    {
        var p = Source("MangaK", 0, Ch(1, "k 1.cbz"), Ch(2, "k 2.cbz"), Ch(3));
        p.AssignArchives([new Models.ProviderArchiveSnapshot { ChapterNumber = 3, ArchiveName = "k 3.cbz" }], completeListing: false);
        Assert.Equal(["k 1.cbz", "k 2.cbz", "k 3.cbz"], p.Chapters.Select(c => c.Filename));
        Assert.All(p.Chapters, c => Assert.False(c.IsDeleted));
    }
}
