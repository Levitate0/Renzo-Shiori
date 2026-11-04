namespace RenzoBackend.Services.Series;

/// <summary>
/// Chapter numbers missing from a series' chapter list, for the "Missing · Ch. 30"
/// rows on the series page (web chapters-section.tsx, Hub SeriesChaptersSection.kt).
///
/// One place, on the server, so the two surfaces cannot flag different gaps.
///
/// What counts as missing, from the owner's examples (2026-11-04):
/// <list type="bullet">
/// <item>27, 28, 29, 31 → 30: a whole number with nothing at all under it.</item>
/// <item>27, 27.1, 27.2, 28.1, 28.2 → 28: the series numbers a chapter N, N.1, N.2…,
/// and N has parts but not its whole.</item>
/// <item>27.1, 27.2, 28.2 → 28.1: a part missing below a part that exists.</item>
/// <item>27, 27.1, 27.2, 29.1, 29.2 → 28 and 29, by the two rules above.</item>
/// </list>
///
/// Guards, each from the live library (385 series):
/// <list type="bullet">
/// <item>A wild top number (2661 after 192, 1000 after 280: a source's typo) would
/// flag thousands; one more than twice the next and 50 above it is left out.</item>
/// <item>.5 and .9 are extras, not parts (580 chapters have only a .5): 4.5 does not
/// make 4.1–4.4 missing.</item>
/// <item>Only one missing part per chapter is flagged; more is a numbering scheme,
/// not a gap.</item>
/// <item>A missing whole is not flagged where two or more sources list the parts and
/// none the whole: that is how those sources number it (World's Finest Assassin:
/// three sources agree there is no 37, only 37.1 and 37.2).</item>
/// <item>Negative numbers are notices and one-shots, not a sequence.</item>
/// </list>
/// </summary>
public static class ChapterGaps
{
    public readonly record struct Range(decimal From, decimal To, int Count);

    /// <summary>A top number this far above the next is a source's typo, not a chapter range.</summary>
    private const decimal OutlierFactor = 2m;
    private const decimal OutlierMargin = 50m;

    /// <summary>
    /// The missing chapters, keyed by the listed number they come right after.
    /// </summary>
    /// <param name="listed">The numbers the list shows.</param>
    /// <summary>A hole wider than this between two listed wholes is a numbering scheme (dates, ids), not missing chapters.</summary>
    private const int MaxHole = 1000;

    /// <param name="wholeAbsentBySources">
    /// Integers whose parts two or more sources list while none lists the whole
    /// (see the class comment); null when unknown.
    /// </param>
    /// <param name="partsListedBySources">
    /// Integers whose parts two or more sources list: a part none of them lists is
    /// their numbering (Komi-san 333 and 333.2 on two sources, no 333.1).
    /// </param>
    public static Dictionary<decimal, List<Range>> Find(IEnumerable<decimal> listed, IReadOnlySet<int>? wholeAbsentBySources = null,
        IReadOnlySet<int>? partsListedBySources = null)
    {
        // Ids and timestamps parsed as chapter numbers carry no sequence.
        List<decimal> nums = listed.Where(n => n >= 0 && n <= int.MaxValue).Distinct().OrderBy(n => n).ToList();
        var result = new Dictionary<decimal, List<Range>>();
        if (nums.Count < 2)
            return result;

        // A small cluster of top chapters far out of line (typos, with any parts
        // of them: 2661 and 2661.1, or two of them) is left out of the analysis;
        // they stay in the list itself. Only ever the top tenth, so a series that
        // really jumps (0, then 99 onwards) keeps its numbers.
        List<decimal> wholeNums = nums.Select(decimal.Truncate).Distinct().ToList();
        int limit = Math.Max(3, wholeNums.Count / 10);
        decimal? cut = null;
        for (int k = wholeNums.Count - 1; k >= 1 && k >= wholeNums.Count - limit; k--)
        {
            if (wholeNums[k] > wholeNums[k - 1] * OutlierFactor && wholeNums[k] > wholeNums[k - 1] + OutlierMargin)
                cut = wholeNums[k];
        }
        if (cut is decimal c)
            nums.RemoveAll(n => decimal.Truncate(n) >= c);
        if (nums.Count < 2)
            return result;

        // Per whole number: is the whole there, and which tenths (N.1 .. N.9).
        var whole = new HashSet<int>();
        var tenths = new Dictionary<int, SortedSet<int>>();
        var present = new HashSet<int>();
        foreach (decimal n in nums)
        {
            int i = (int)decimal.Truncate(n);
            present.Add(i);
            decimal frac = n - i;
            if (frac == 0)
                whole.Add(i);
            else if (frac * 10 == decimal.Truncate(frac * 10))
            {
                if (!tenths.TryGetValue(i, out SortedSet<int>? t))
                    tenths[i] = t = [];
                t.Add((int)(frac * 10));
            }
        }

        // Parts proper: .5 and up are extras unless .4 is there (a .5/.6 pair is an
        // extras block, not parts 5 and 6 with a 4 missing), and .9 is one unless .8 is.
        SortedSet<int> Parts(int i)
        {
            if (!tenths.TryGetValue(i, out SortedSet<int>? t))
                return [];
            var p = new SortedSet<int>(t);
            if (!p.Contains(4))
                p.RemoveWhere(x => x >= 5);
            if (p.Contains(9) && !p.Contains(8))
                p.Remove(9);
            return p;
        }

        List<int> partChapters = present.Where(i => Parts(i).Count > 0).OrderBy(i => i).ToList();
        int withWhole = partChapters.Count(whole.Contains);
        bool usesParts = partChapters.Any(i => Parts(i).IsSupersetOf([1, 2]));

        var missing = new SortedSet<decimal>();

        // 1. A whole number with nothing under it, between the first and last:
        //    the holes between neighbouring listed wholes.
        List<int> sortedPresent = present.OrderBy(i => i).ToList();
        for (int k = 1; k < sortedPresent.Count; k++)
        {
            int hole = sortedPresent[k] - sortedPresent[k - 1] - 1;
            if (hole <= 0 || hole > MaxHole)
                continue;
            for (int i = sortedPresent[k - 1] + 1; i < sortedPresent[k]; i++)
                missing.Add(i);
        }

        // 2. A chapter numbered in parts that lacks its whole, where this series
        //    gives its chapters a whole: the nearest earlier part chapter has one,
        //    and at least half of them do.
        if (partChapters.Count >= 2 && withWhole * 2 >= partChapters.Count)
        {
            for (int k = 1; k < partChapters.Count; k++)
            {
                int i = partChapters[k];
                if (whole.Contains(i) || !Parts(i).Contains(1) || !whole.Contains(partChapters[k - 1]))
                    continue;
                if (wholeAbsentBySources?.Contains(i) == true)
                    continue;
                missing.Add(i);
            }
        }

        // 3. One part missing below a part that exists, in a series numbered in parts.
        if (usesParts)
        {
            foreach (int i in partChapters)
            {
                if (partsListedBySources?.Contains(i) == true)
                    continue;
                SortedSet<int> p = Parts(i);
                int top = p.Max;
                List<int> gaps = Enumerable.Range(1, top - 1).Where(j => !p.Contains(j)).ToList();
                if (gaps.Count == 1)
                    missing.Add(i + gaps[0] / 10m);
            }
        }

        // Place each missing number after the listed number just below it, and
        // join neighbours there into runs: consecutive wholes, or consecutive
        // parts of one chapter.
        foreach (decimal m in missing)
        {
            int at = nums.BinarySearch(m);
            if (at >= 0)
                continue; // listed after all
            int below = ~at - 1;
            if (below < 0)
                continue;
            decimal after = nums[below];
            if (!result.TryGetValue(after, out List<Range>? runs))
                result[after] = runs = [];
            if (runs.Count > 0 && Follows(runs[^1].To, m))
                runs[^1] = runs[^1] with { To = m, Count = runs[^1].Count + 1 };
            else
                runs.Add(new Range(m, m, 1));
        }
        return result;
    }

    /// <summary>Is <paramref name="next"/> the number right after <paramref name="last"/> in its kind (wholes, or tenths of one chapter)?</summary>
    private static bool Follows(decimal last, decimal next)
    {
        bool lastWhole = last == decimal.Truncate(last), nextWhole = next == decimal.Truncate(next);
        if (lastWhole && nextWhole)
            return next == last + 1;
        if (!lastWhole && !nextWhole)
            return decimal.Truncate(last) == decimal.Truncate(next) && next == last + 0.1m;
        return false;
    }
}
