using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>Missing-chapter labels (the owner's four examples, 2026-11-04, and the live library's traps).</summary>
public class ChapterGapsTests
{
    private static string Gaps(params decimal[] listed) => Gaps(listed, null);

    private static string Gaps(decimal[] listed, IReadOnlySet<int>? agree) =>
        string.Join(" ", ChapterGaps.Find(listed, agree)
            .OrderBy(kv => kv.Key)
            .SelectMany(kv => kv.Value.Select(r => $"{kv.Key}:{r.From}-{r.To}x{r.Count}")));

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
}
