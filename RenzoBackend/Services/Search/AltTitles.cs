using System.Text.RegularExpressions;

namespace RenzoBackend.Services.Search;

/// <summary>
/// A series' other names, read from what a source site says about it.
///
/// Many sites list them, not all, and none in one agreed form, so this reads the
/// common shapes out of the series description:
/// <list type="bullet">
/// <item>a labelled line: "Alternative Names: A; B / C", "Associated Name(s): A, B",
/// "Other titles: …", "Also known as …", "AKA: …";</item>
/// <item>a label on its own line followed by one name per line, bulleted or not, up
/// to a blank line (MangaDex's "Alternative titles:" then "• A", "• B").</item>
/// </list>
/// Used by the add-series search (a result whose title matches what was searched
/// has its other names searched too) and by source migration (a series is looked
/// for under every name it has).
/// </summary>
public static class AltTitles
{
    private static readonly Regex Label = new(
        @"^[\s*•·\-–\[\(]*(?<label>(?:alternative|alternate|alt\.?)(?:\s+(?:names?|titles?)(?:\s*\(s\))?)?|(?:associated|other)\s+(?:names?|titles?)(?:\s*\(s\))?|also\s+known\s+as|a\.?k\.?a\.?|synonyms?)(?![\p{L}])\s*(?<colon>[:：])?\s*(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>A line that is itself some other "Label: value" (Status:, Author:), which ends a name list.</summary>
    private static readonly Regex OtherField = new(@"^[\p{L} ]{2,20}\s*[:：]", RegexOptions.CultureInvariant);

    private static readonly char[] Bullets = ['•', '·', '-', '–', '*', '>', ' ', '\t', '"', '“', '”', '\''];

    /// <summary>Most names returned; each costs a search per source.</summary>
    public const int MaxNames = 8;

    /// <summary>
    /// The other names in <paramref name="description"/>, without the
    /// <paramref name="exclude"/> names (compared by <see cref="Key"/>), at most
    /// <see cref="MaxNames"/>, names written in Latin letters first (what English
    /// sources can find).
    /// </summary>
    public static List<string> Parse(string? description, params string?[] exclude)
    {
        var found = new List<string>();
        if (string.IsNullOrWhiteSpace(description))
            return found;
        var skip = new HashSet<string>(exclude.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => Key(e!)), StringComparer.Ordinal);
        string[] lines = description.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match m = Label.Match(Plain(lines[i]));
            if (!m.Success)
                continue;
            // Names on the label's line need a colon right after the label:
            // "Alternatively, she …" or "Also known as X, the story follows …" in
            // a synopsis is not a list of names.
            string rest = RepeatedLabel.Replace(m.Groups["rest"].Value.Trim(), "");
            if (rest.Length > 0)
            {
                if (m.Groups["colon"].Success)
                    found.AddRange(SplitNames(rest, allowComma: true, skip));
                continue;
            }
            // A label alone, then one name per line: each line is one name, commas
            // and all ("- Please Bully Me, Miss Villainess!").
            int j = i + 1;
            for (; j < lines.Length; j++)
            {
                string next = Plain(lines[j]);
                if (next.Length == 0 || OtherField.IsMatch(next))
                    break;
                found.AddRange(SplitNames(next, allowComma: false, skip));
            }
            i = j - 1; // a listed name is not read again as a label ("- Aka no …")
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        return found
            .Select(Clean)
            .Where(n => n.Length >= 2 && n.Length <= 150 && n.Any(char.IsLetter))
            .Where(n => { string k = Key(n); return k.Length > 0 && !skip.Contains(k) && !Placeholders.Contains(k) && !Related(k, skip) && seen.Add(k); })
            .OrderByDescending(Latin)
            .Take(MaxNames)
            .ToList();
    }

    /// <summary>
    /// A line without Markdown emphasis: "**Alternative Titles**:" (XCOMIC) and
    /// "**Associated Name(s):**" (ReiManga, Kagane, Valir) are labels like any other.
    /// </summary>
    private static string Plain(string line) => Emphasis.Replace(line, "").Trim();

    private static readonly Regex Emphasis = new(@"\*\*|__", RegexOptions.CultureInvariant);

    /// <summary>
    /// The series' own name with a word or more added or taken away: a related
    /// work, not another name for it. Kagane's "Junior High School DxD" lists
    /// "High School DxD" (the series it spun off from); "Solo Leveling: Ragnarok"
    /// is a sequel. A leftover under four letters ("The …") is still the series.
    /// </summary>
    private static bool Related(string key, HashSet<string> own) =>
        own.Any(o => o.Length > 0 && o != key
            && (o.Length > key.Length ? (o.StartsWith(key) || o.EndsWith(key)) && o.Length - key.Length >= 4
                                      : (key.StartsWith(o) || key.EndsWith(o)) && key.Length - o.Length >= 4));

    /// <summary>
    /// Written in Latin letters, what English sources can find: at least 80% of
    /// its letters plain ASCII, a "(Japanese)" style tag left out. Indonesian or
    /// Italian names pass, as they can be site titles; "とんがり帽子のアトリエ
    /// (Japanese)", "Средняя школа DxD" and pinyin with tone marks do not.
    /// </summary>
    public static bool Latin(string name)
    {
        string bare = Regex.Replace(name, @"\([^)]*\)", "");
        int letters = bare.Count(char.IsLetter);
        return letters >= 2 && bare.Count(c => c < 128 && char.IsLetter(c)) >= letters * 0.8;
    }

    /// <summary>What sites write when they have no other name: "Alternative Names: Unknown" is on 1,400 cached series.</summary>
    private static readonly HashSet<string> Placeholders = new(StringComparer.Ordinal)
    {
        "unknown", "none", "na", "nothing", "updating", "tba", "tbd", "null", "nil", "notavailable",
        "noalternativename", "noalternativenames", "alternative", "alternate", "alt", "other", "names", "titles",
    };

    /// <summary>A label repeated at the start of the value (Manganato: "Alternative Name: Alternative : X").</summary>
    private static readonly Regex RepeatedLabel = new(
        @"^(?:(?:alternative|alternate|alt\.?|associated|other)\s*(?:names?|titles?)?(?:\s*\(s\))?\s*[:：]\s*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static IEnumerable<string> SplitNames(string value, bool allowComma, HashSet<string> skip)
    {
        // Separators sites use between names. A comma only on a "Label: A, B" line
        // and only when nothing stronger is there: plenty of single titles contain
        // one ("Welcome Back, Papa").
        string[] strong = [";", " / ", "|", "•", "、", "；"];
        if (strong.Any(value.Contains))
            return value.Split(strong, StringSplitOptions.RemoveEmptyEntries);
        if (!allowComma || !value.Contains(", "))
            return [value];
        // The series' own title with a comma in it is not two names.
        if (skip.Contains(Key(value)))
            return [];
        // Nor are pieces of it ("One Day" out of "One Day, the Male Lead Fell …").
        return value.Split(", ", StringSplitOptions.RemoveEmptyEntries)
            .Where(piece => { string k = Key(piece); return !skip.Any(s => s.Length > k.Length && (s.StartsWith(k) || s.EndsWith(k))); });
    }

    /// <summary>
    /// A name specific enough that a source title equal to it is the same series:
    /// two words or more and eight letters or more. "Unknown", "Dear", "Nana" or
    /// "One Day" name other works too.
    /// </summary>
    public static bool Distinctive(string name) =>
        Key(name).Length >= 8 && name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2;

    private static string Clean(string name) =>
        Regex.Replace(name.Trim().Trim(Bullets).TrimEnd('.', ',', ';').Trim(), @"\s+", " ");

    /// <summary>
    /// A name reduced to what must match for two names to be the same title:
    /// letters and digits, lower case, "&amp;" read as "and" and "×" as "x"
    /// ("SPY×FAMILY" is "Spy x Family"). Stricter than
    /// StringExtensions.NormalizeTitle, which drops "a " inside words; a search
    /// result is only taken as the series when this is equal.
    /// </summary>
    public static string Key(string? title) =>
        string.IsNullOrEmpty(title)
            ? string.Empty
            : new string(title.ToLowerInvariant().Replace("&", "and").Replace('×', 'x').Replace('✕', 'x').Replace('✖', 'x')
                .Where(char.IsLetterOrDigit).ToArray());
}
