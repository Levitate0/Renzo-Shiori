namespace RenzoBackend.Services.Helpers;

/// <summary>
/// Genre/tag-based classifier for explicit adult (18+) content, used to compute
/// the <c>isNsfw</c> detection flag on series DTOs.
///
/// Matches on explicit RATING/act tags only. Ecchi, mature, suggestive, seinen,
/// josei and harem are deliberately NOT matched — the feature targets 18+, not
/// fanservice — and neither are orientation genres (yaoi/yuri/BL/GL) or formats
/// (doujinshi), which describe what a work is about rather than how explicit it
/// is. Compound tags don't trip it either: "Adult Protagonist" and "Martial
/// Arts" are matched as whole tags, not substrings.
///
/// Mirrors the frontend list in lib/utils/adult-filter.ts and the native one in
/// Renzo Hub's AdultFilter.kt — all three must be changed together.
///
/// The Mihon extension-level NSFW flag is intentionally not used as a signal.
/// That is not an assumption: on this install 178 of 189 providers (94%) carry
/// it, covering 99.2% of catalogued rows, so it would hide essentially the whole
/// catalogue.
/// </summary>
public static class AdultContentClassifier
{
    private static readonly HashSet<string> AdultTags = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ratings
        "hentai", "erotica", "erotic", "adult", "smut", "pornographic", "porn",
        "18+", "r18", "r-18", "r18+", "r-18g", "nsfw", "adult (18+)", "explicit",

        // Explicit acts/kinks. These carry real volume in the live catalogue and
        // were all being missed: a source that tags "Blowjob" but not "Adult"
        // would sail straight through a hide-18+ filter.
        "blowjob", "double penetration", "sex toys", "sexual violence",
        "sexual abuse", "rape", "incest", "netorare", "ntr", "bdsm",
        "bestiality", "futanari", "shemale", "dickgirl", "milf", "nudity",

        // Sexualised minors. Non-negotiable — these must never be reachable with
        // 18+ hidden, and several are common enough to matter (loli/lolicon and
        // shota/shotacon together account for ~3,000 catalogued rows).
        "loli", "lolicon", "shota", "shotacon",
    };

    /// <summary>True when any tag is an explicit adult (18+) rating.</summary>
    public static bool IsAdult(IEnumerable<string>? genres)
    {
        if (genres == null)
            return false;
        foreach (string g in genres)
        {
            if (string.IsNullOrWhiteSpace(g))
                continue;
            foreach (string part in Parts(g))
            {
                if (AdultTags.Contains(part))
                    return true;
            }
        }
        return false;
    }

    /// <summary>True when a single tag name is an explicit adult (18+) rating.</summary>
    public static bool IsAdultTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return false;
        foreach (string part in Parts(tag))
        {
            if (AdultTags.Contains(part))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The comparable pieces of a raw tag. Sources dress the same rating up in
    /// different ways, and an exact-match set misses all of them:
    ///   • MangaDex-style prefixes — "Content rating: Pornographic"
    ///   • bundled alternatives    — "Futanari | Shemale | Dickgirl", "Gay / Yaoi"
    /// so the prefix is stripped and the alternatives are split apart, and each
    /// piece is matched on its own.
    /// </summary>
    private static IEnumerable<string> Parts(string raw)
    {
        string tag = raw.Trim();

        int colon = tag.IndexOf(':');
        if (colon > 0 && colon < tag.Length - 1)
        {
            string prefix = tag[..colon].Trim();
            if (prefix.Equals("content rating", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("rating", StringComparison.OrdinalIgnoreCase) ||
                prefix.Equals("genre", StringComparison.OrdinalIgnoreCase))
            {
                tag = tag[(colon + 1)..].Trim();
            }
        }

        yield return tag;
        if (tag.IndexOf('|') < 0 && tag.IndexOf('/') < 0)
            yield break;

        foreach (string part in tag.Split('|', '/'))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0)
                yield return trimmed;
        }
    }
}
