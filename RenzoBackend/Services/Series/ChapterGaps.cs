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
/// A series' numbering can change for a stretch of the list and change back ("the
/// chapter naming scheme changes i.e. 28.1 then 29.1", "they can shift for parts of
/// the chapter list"), so a chapter is judged by its own stretch, never by a vote
/// over the whole series or by chapters far off in it. A stretch is two or more part
/// chapters next to each other (28, 29, 30; a hole between them is a chapter missing
/// from it, a whole listed between them ends it), numbered the same way:
/// <list type="bullet">
/// <item>28, 28.1, 29.1 → nothing: a stretch of chapters with only a .1, no more than
/// half of them with a whole, is numbered that way; the .1 is the chapter. EXP Is
/// Golden: MangaHub lists "Chapter 28", "Chapter 28.1", "Chapter 29.1" after 1–27 (10
/// and 12 there really are split: 10, 10.1, 10.2).</item>
/// <item>10, 10.1, 11, 11.1, 12, 12.1, 13.1 → 13: where most of such a stretch has a
/// whole, the .1 is a part and a .1 alone lacks its whole (The Executioner and Her
/// Way of Life: 1, 1.1, 2, 2.1 … 44, 44.1). So does a .1 just before a chapter in
/// parts that has its whole: 29, 29.1, 29.2, 30.1, 31, 31.1 → 30.</item>
/// <item>28.1, 29.1, 31.1 → 30.1: a hole inside such a stretch is in its scheme.</item>
/// <item>5, 5.1, 5.2, 6.1, 6.2, 7.1, 7.2, 8 → nothing: chapters in parts with no
/// whole are the list switching scheme, not wholes missing (Majutsushi Kunon:
/// MangaPill's 38–40 are Mangafreak's "38a", "38b"…, and from 41 on only
/// Mangafreak's halves exist).</item>
/// <item>20.1, 21.1, 22.1, 23, 24, 24.1, 24.2, 25.1, 25.2 → 25: one chapter alone without
/// its whole, after one with it, is still the rule above, however the rest of the
/// series is numbered.</item>
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
        var parts = new Dictionary<int, SortedSet<int>>();
        foreach ((int i, SortedSet<int> t) in tenths)
        {
            var p = new SortedSet<int>(t);
            if (!p.Contains(4))
                p.RemoveWhere(x => x >= 5);
            if (p.Contains(9) && !p.Contains(8))
                p.Remove(9);
            if (p.Count > 0)
                parts[i] = p;
        }
        SortedSet<int> Parts(int i) => parts.TryGetValue(i, out SortedSet<int>? p) ? p : [];

        // The chapters numbered in parts, in order, and for each whether it sits right
        // after the one before: nothing listed between them, at most a hole.
        List<int> sortedPresent = present.OrderBy(i => i).ToList();
        var partChapters = new List<int>();
        var beside = new List<bool>();
        for (int j = 0; j < sortedPresent.Count; j++)
        {
            if (!parts.ContainsKey(sortedPresent[j]))
                continue;
            beside.Add(partChapters.Count > 0 && sortedPresent[j - 1] == partChapters[^1]);
            partChapters.Add(sortedPresent[j]);
        }
        bool usesParts = partChapters.Any(i => Parts(i).IsSupersetOf([1, 2]));

        // Stretches: two or more part chapters next to each other in the list (28, 29,
        // 30, or with only a hole between them) numbered the same way. A series can
        // switch scheme for part of its list and switch back, so each chapter is
        // judged by its own stretch, never by chapters further off: a whole listed
        // between two part chapters ends a stretch (Secret Saint's 30, 30.1 and 40,
        // 40.1 are no stretch).
        bool PointOne(int i) => Parts(i) is { Count: 1, Min: 1 };
        bool NoWhole(int i) => !whole.Contains(i);
        // For each part chapter, the first and last index of its stretch numbered
        // that way (itself alone when it is in none).
        (int[] From, int[] To) Stretches(Func<int, bool> numbered)
        {
            int n = partChapters.Count;
            int[] from = new int[n], to = new int[n];
            bool Joined(int k) => beside[k] && numbered(partChapters[k - 1]) && numbered(partChapters[k]);
            for (int k = 0; k < n; k++)
                from[k] = k > 0 && Joined(k) ? from[k - 1] : k;
            for (int k = n - 1; k >= 0; k--)
                to[k] = k + 1 < n && Joined(k + 1) ? to[k + 1] : k;
            return (from, to);
        }

        var missing = new SortedSet<decimal>();

        // 1. A whole number with nothing under it, between the first and last:
        //    the holes between neighbouring listed wholes. Between two chapters
        //    numbered N.1 alone (28.1, 29.1, 31.1) the hole is in that scheme: 30.1.
        for (int k = 1; k < sortedPresent.Count; k++)
        {
            int below = sortedPresent[k - 1], above = sortedPresent[k];
            int hole = above - below - 1;
            if (hole <= 0 || hole > MaxHole)
                continue;
            decimal part = PointOne(below) && NoWhole(below) && PointOne(above) && NoWhole(above) ? 0.1m : 0m;
            for (int i = below + 1; i < above; i++)
                missing.Add(i + part);
        }

        // 2. A chapter numbered in parts that lacks its whole, where the chapters
        //    before it have one: the nearest earlier part chapter does. Not where
        //    the list switches scheme at it: it begins a stretch of parts with no
        //    whole (6.1, 6.2, 7.1, 7.2), or it ends a stretch numbered N.1 alone of
        //    which at most half has a whole, with no chapter in parts right after
        //    it (28, 28.1, 29.1: the .1 is the chapter's number). Where most of a
        //    .1 stretch has a whole (1, 1.1, 2, 2.1…), or the part chapter right
        //    after it does (30.1, 31, 31.1), the .1 is a part and the whole is
        //    missing.
        (int[] From, int[] To) noWhole = Stretches(NoWhole), pointOne = Stretches(PointOne);
        for (int k = 1; k < partChapters.Count; k++)
        {
            int i = partChapters[k];
            if (whole.Contains(i) || !Parts(i).Contains(1) || !whole.Contains(partChapters[k - 1]))
                continue;
            if (noWhole.To[k] > noWhole.From[k])
                continue;
            bool partsAfter = k + 1 < partChapters.Count && beside[k + 1];
            int first = pointOne.From[k], count = k - first + 1;
            if (!partsAfter && count >= 2 && partChapters.GetRange(first, count).Count(whole.Contains) * 2 <= count)
                continue;
            if (wholeAbsentBySources?.Contains(i) == true)
                continue;
            missing.Add(i);
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
        // join neighbours there into runs: consecutive wholes, consecutive parts
        // of one chapter, or the .1s of a hole in a .1 stretch (30.1, 31.1).
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

    /// <summary>
    /// Is <paramref name="next"/> the number right after <paramref name="last"/> in its kind
    /// (wholes, tenths of one chapter, or the .1 chapters of a hole in a .1 stretch)?
    /// </summary>
    private static bool Follows(decimal last, decimal next)
    {
        bool lastWhole = last == decimal.Truncate(last), nextWhole = next == decimal.Truncate(next);
        if (lastWhole && nextWhole)
            return next == last + 1;
        if (!lastWhole && !nextWhole)
            return (decimal.Truncate(last) == decimal.Truncate(next) && next == last + 0.1m)
                || (next == last + 1 && last - decimal.Truncate(last) == 0.1m);
        return false;
    }
}
