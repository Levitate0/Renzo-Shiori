namespace RensaioBackend.Services.Helpers;

/// <summary>
/// Genre/tag-based classifier for explicit adult (18+) content, used to compute
/// the <c>isNsfw</c> detection flag on series DTOs. Exact-token matching on
/// explicit rating tags only — ecchi/mature/suggestive are deliberately NOT
/// matched (fanservice is not 18+), and compound tags like "Adult Protagonist"
/// don't trip it. Mirrors the frontend list in lib/utils/adult-filter.ts; the
/// server-side copy exists so detection can aggregate tags across ALL of a
/// series' sources (which the client never sees) without shipping them.
/// The Mihon extension-level NSFW flag is intentionally not used as a signal:
/// it marks any aggregator carrying any 18+ content, which in practice is
/// nearly every source.
/// </summary>
public static class AdultContentClassifier
{
    private static readonly HashSet<string> AdultTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "hentai",
        "erotica",
        "adult",
        "smut",
        "pornographic",
        "porn",
        "18+",
        "r18",
        "r-18",
        "r18+",
        "r-18g",
        "nsfw",
    };

    /// <summary>True when any tag is an explicit adult (18+) rating.</summary>
    public static bool IsAdult(IEnumerable<string>? genres)
    {
        if (genres == null)
            return false;
        foreach (string g in genres)
        {
            if (!string.IsNullOrWhiteSpace(g) && AdultTags.Contains(g.Trim()))
                return true;
        }
        return false;
    }
}
