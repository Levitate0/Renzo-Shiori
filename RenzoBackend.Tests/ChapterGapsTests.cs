using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>Missing-chapter labels (the owner's examples, 2026-11-04, numbering that shifts for a stretch of the list, and the live library's traps).</summary>
public class ChapterGapsTests
{
    private static string Gaps(params decimal[] listed) => Gaps(listed, null);

    private static string Gaps(decimal[] listed, IReadOnlySet<int>? agree) =>
        string.Join(" ", ChapterGaps.Find(listed, agree)
            .OrderBy(kv => kv.Key)
            .SelectMany(kv => kv.Value.Select(r => $"{kv.Key}:{r.From}-{r.To}x{r.Count}")));

    private static decimal[] Wholes(int from, int to) => [.. Enumerable.Range(from, to - from + 1).Select(i => (decimal)i)];

    [Fact]
    public void A_whole_number_with_nothing_under_it() =>
        Assert.Equal("29:30-30x1", Gaps(27, 28, 29, 31, 32));

    [Fact]
    public void Whole_then_parts_and_a_chapter_gone_entirely() =>
        Assert.Equal("27.2:28-29x2", Gaps(27, 27.1m, 27.2m, 29.1m, 29.2m));

    [Fact]
    public void A_series_that_gives_its_chapters_a_whole_number() =>
        Assert.Equal("27.2:28-28x1", Gaps(27, 27.1m, 27.2m, 28.1m, 28.2m));

    [Fact]
    public void A_series_numbered_only_in_parts() =>
        Assert.Equal("27.2:28.1-28.1x1", Gaps(27.1m, 27.2m, 28.2m));

    [Fact]
    public void Nothing_is_missing_in_an_unbroken_list() =>
        Assert.Equal("", Gaps(0, 1, 2, 2.5m, 3, 4, 4.1m, 4.2m, 5, 5.1m, 5.2m));

    [Fact]
    public void A_long_hole_is_one_label() =>
        Assert.Equal("21:22-60x39", Gaps([.. Enumerable.Range(1, 21).Select(i => (decimal)i), 61m]));

    [Fact]
    public void A_typo_top_number_is_ignored()
    {
        // Comic Asura listed "Chapter 2661" right after 192.
        Assert.Equal("", Gaps([.. Enumerable.Range(1, 192).Select(i => (decimal)i), 2661m, 2661.1m]));
        Assert.Equal("183:184-184x1", Gaps([.. Enumerable.Range(1, 192).Where(i => i != 184).Select(i => (decimal)i), 2661m]));
    }

    [Fact]
    public void Point_five_and_point_nine_are_extras_not_parts() =>
        // Kage no Jitsuryokusha: 4.1, 4.5 — not "4.2-4.4 missing". Slime: 11.5, 11.9.
        Assert.Equal("", Gaps(3, 4, 4.1m, 4.5m, 5, 11, 11.5m, 11.9m, 6, 7, 8, 9, 10, 12, 12.1m, 12.2m));

    [Fact]
    public void More_than_one_missing_part_is_a_numbering_scheme() =>
        Assert.Equal("", Gaps(1.1m, 1.2m, 2.1m, 2.4m, 3.1m, 3.2m));

    [Fact]
    public void Sources_agreeing_on_no_whole_number_are_believed() =>
        // World's Finest Assassin: three sources list 37.1 and 37.2, none 37.
        Assert.Equal("", Gaps([36, 36.1m, 36.2m, 37.1m, 37.2m, 38], new HashSet<int> { 37 }));

    [Fact]
    public void Notices_with_negative_numbers_are_not_a_sequence() =>
        Assert.Equal("", Gaps(-4, -1, 0, 1, 2));

    [Fact]
    public void Fewer_than_two_chapters_have_no_gaps()
    {
        Assert.Equal("", Gaps());
        Assert.Equal("", Gaps(5));
    }

    [Fact]
    public void Two_typo_top_numbers_are_ignored_too() =>
        Assert.Equal("183:184-184x1", Gaps([.. Enumerable.Range(1, 192).Where(i => i != 184).Select(i => (decimal)i), 2661m, 2662m]));

    [Fact]
    public void A_series_that_really_jumps_keeps_its_numbers() =>
        // 0, then 99 onwards: the jump is the bulk of the list, not a typo cluster.
        Assert.Equal("0:1-98x98", Gaps([0m, .. Enumerable.Range(99, 7).Select(i => (decimal)i)]));

    [Fact]
    public void A_point_five_point_six_pair_is_extras() =>
        // I'm in Love With the Villainess: 10.1-10.3, 10.5, 10.6 — no 10.4 missing.
        Assert.Equal("", Gaps(9, 10, 10.1m, 10.2m, 10.3m, 10.5m, 10.6m, 11));

    [Fact]
    public void Ids_and_huge_numbers_do_not_break_anything() =>
        Assert.Equal("", Gaps(1, 2, 3000000000m, 3000000001m, 20240105m, 20240112m));

    [Fact]
    public void A_hole_too_wide_to_be_chapters_is_not_listed() =>
        Assert.Equal("", Gaps(1, 2, 5000, 5001, 5002, 5003, 5004, 5005, 5006, 5007, 5008, 5009, 5010));

    [Fact]
    public void Sources_agreeing_on_missing_parts_are_believed() =>
        // Komi-san: two sources list 333 and 333.2, neither 333.1.
        Assert.Equal("", string.Join(" ", ChapterGaps.Find([332, 332.1m, 332.2m, 333, 333.2m, 334], null, new HashSet<int> { 333 })
            .SelectMany(kv => kv.Value.Select(r => r.From.ToString()))));

    // The owner, 2026-11-04: "the chapter naming scheme changes i.e. 28.1 then 29.1",
    // "they can shift for parts of the chapter list".

    [Fact]
    public void A_point_one_stretch_after_a_whole_is_the_numbering() =>
        // EXP Is Golden: MangaKatana 1-27 with 3.5, MangaHub "Chapter 28", "Chapter 28.1", "Chapter 29.1".
        Assert.Equal("", Gaps([.. Wholes(1, 9), 3.5m, 10, 10.1m, 10.2m, 11, 12, 12.1m, 12.2m, .. Wholes(13, 28), 28.1m, 29.1m]));

    [Fact]
    public void A_point_one_stretch_can_shift_back_to_wholes() =>
        Assert.Equal("", Gaps([.. Wholes(1, 9), 10, 10.1m, 10.2m, .. Wholes(11, 28), 28.1m, 29.1m, 30.1m, 31, 32]));

    [Fact]
    public void A_point_one_stretch_needs_no_whole_at_its_start() =>
        Assert.Equal("", Gaps([.. Wholes(1, 27), 28.1m, 29.1m, 30.1m, 31, 32, 33]));

    [Fact]
    public void A_hole_in_a_point_one_stretch_is_in_its_scheme() =>
        Assert.Equal("29.1:30.1-30.1x1", Gaps([.. Wholes(1, 27), 28.1m, 29.1m, 31.1m, 32.1m]));

    [Fact]
    public void A_longer_hole_in_a_point_one_stretch_is_one_label() =>
        Assert.Equal("29.1:30.1-31.1x2", Gaps([.. Wholes(1, 27), 28.1m, 29.1m, 32.1m, 33.1m]));

    [Fact]
    public void A_lone_point_one_after_split_chapters_with_wholes_lacks_its_whole() =>
        // 39.2 may simply not be out yet: the owner's 27, 27.1, 27.2, 28.1, 28.2 → 28.
        Assert.Equal("38.2:39-39x1", Gaps(36, 37, 37.1m, 37.2m, 38, 38.1m, 38.2m, 39.1m));

    [Fact]
    public void A_stretch_back_to_wholes_is_judged_on_its_own() =>
        // 4 of the 5 part chapters have no whole, so the old series-wide vote missed
        // it; 24 and 25 are split chapters after the .1 stretch, and 24 has one.
        Assert.Equal("24.2:25-25x1", Gaps(18, 19, 20.1m, 21.1m, 22.1m, 23, 24, 24.1m, 24.2m, 25.1m, 25.2m));

    [Fact]
    public void A_stretch_of_parts_with_no_whole_is_a_scheme() =>
        // 6-8 switch to parts with no whole and 9 switches back: not 6, 7 and 8 missing.
        Assert.Equal("", Gaps(4, 5, 5.1m, 5.2m, 6.1m, 6.2m, 7.1m, 7.2m, 8.1m, 8.2m, 9, 10));

    [Fact]
    public void A_whole_listed_between_ends_a_stretch()
    {
        // A Tale of the Secret Saint with its 30 gone: 30.1 and 40.1 are ten chapters apart, no stretch.
        Assert.Equal("29:30-30x1", Gaps([.. Wholes(1, 21), 21.1m, 21.2m, 21.3m, .. Wholes(22, 29), 30.1m, .. Wholes(31, 40), 40.1m, 41]));
        // The owner's 27, 27.1, 27.2, 28.1, 28.2 → 28, with a newest 32 out in parts only.
        Assert.Equal("27.2:28-28x1", Gaps([1, 1.1m, 1.2m, .. Wholes(2, 27), 27.1m, 27.2m, 28.1m, 28.2m, 29, 30, 31, 32.1m, 32.2m]));
    }

    [Fact]
    public void A_hole_does_not_end_a_stretch() =>
        // 7 is gone from the stretch of parts with no whole; 6 is still in it.
        Assert.Equal("6.2:7-7x1", Gaps(4, 5, 5.1m, 5.2m, 6.1m, 6.2m, 8.1m, 8.2m, 9, 10));

    [Fact]
    public void A_point_one_alone_where_the_point_ones_have_a_whole_lacks_its_whole()
    {
        // The Executioner and Her Way of Life numbers 1, 1.1, 2, 2.1 … 44, 44.1.
        Assert.Equal("12.1:13-13x1", Gaps(10, 10.1m, 11, 11.1m, 12, 12.1m, 13.1m));
        // Different World Slow Life with its 30 gone: the next .1 has its whole.
        Assert.Equal("29.2:30-30x1", Gaps(29, 29.1m, 29.2m, 30.1m, 31, 31.1m, 32, 32.1m, 32.2m));
    }
}
