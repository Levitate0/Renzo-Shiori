namespace RenzoBackend.Services.Series;

/// <summary>
/// Best-effort classification of a series into one of the configured category
/// folders (Manga / Manhwa / Manhua / …). Type metadata is frequently missing
/// from source extensions, so this leans on two weak-but-real signals and
/// deliberately biases toward "Manga" (the dominant, least-surprising bucket)
/// whenever the evidence isn't strong — mislabelling a manhwa as manga is an
/// easy one-click fix in the UI, whereas the reverse scatters Japanese titles
/// into the wrong shelf.
///
/// Priority:
///   1. An explicit genre/tag that names the format ("manhua", "manhwa",
///      "webtoon"/"long strip" → manhwa, "manga").
///   2. The backing source(s): a handful of extensions are exclusively Korean
///      (manhwa) or Chinese (manhua) aggregators. Mixed aggregators that carry
///      Japanese titles too (MangaDex, Comic Asura, MangaKatana, …) are NOT
///      treated as a signal — they'd produce false manhwa labels.
///   3. Otherwise the default category (Manga if present, else the first
///      configured category).
///
/// Only ever returns a value that exists in <paramref name="configuredCategories"/>
/// (case-insensitive) so a user who renamed/removed categories never gets a
/// folder outside their own scheme; returns null when categorization is off or
/// no categories are configured.
/// </summary>
public static class SeriesTypeClassifier
{
    // Sources that only ever carry Korean webtoons — a reliable manhwa signal.
    private static readonly HashSet<string> ManhwaOnlySources = new(StringComparer.OrdinalIgnoreCase)
    {
        "EZmanga", "ManhwaBuddy", "Asura Scans", "Anisa Scans", "Arena Scans",
        "Galaxy Manga", "King of Shojo", "Manhwa Clan", "ManhwaClan", "Manhwatop",
    };

    // Sources that only ever carry Chinese manhua.
    private static readonly HashSet<string> ManhuaOnlySources = new(StringComparer.OrdinalIgnoreCase)
    {
        "ManhuaFast", "ManhuaPlus", "ManhuaScan", "ManhuaUS", "1st Kiss Manhua",
    };

    public static string? Classify(
        IEnumerable<string>? genres,
        IEnumerable<string>? providerNames,
        string[]? configuredCategories)
    {
        if (configuredCategories == null || configuredCategories.Length == 0)
            return null;

        string? Resolve(string wanted) =>
            configuredCategories.FirstOrDefault(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase));

        // 1) Explicit format genre/tag.
        if (genres != null)
        {
            foreach (string g in genres)
            {
                if (string.IsNullOrWhiteSpace(g)) continue;
                string n = g.Trim().ToLowerInvariant();
                if (n.Contains("manhua"))
                    return Resolve("Manhua") ?? DefaultCategory(configuredCategories);
                if (n.Contains("manhwa") || n.Contains("webtoon") || n.Contains("long strip"))
                    return Resolve("Manhwa") ?? DefaultCategory(configuredCategories);
                if (n.Contains("manga"))
                    return Resolve("Manga") ?? DefaultCategory(configuredCategories);
            }
        }

        // 2) Source-based signal — only when EVERY resolvable source agrees, so a
        // mixed aggregator riding alongside a manhwa-only source doesn't tip it.
        if (providerNames != null)
        {
            var names = providerNames.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (names.Count > 0)
            {
                if (names.Any(ManhuaOnlySources.Contains) && !names.Any(ManhwaOnlySources.Contains))
                {
                    string? manhua = Resolve("Manhua");
                    if (manhua != null) return manhua;
                }
                if (names.Any(ManhwaOnlySources.Contains) && !names.Any(ManhuaOnlySources.Contains))
                {
                    string? manhwa = Resolve("Manhwa");
                    if (manhwa != null) return manhwa;
                }
            }
        }

        // 3) Default.
        return DefaultCategory(configuredCategories);
    }

    private static string DefaultCategory(string[] configuredCategories) =>
        configuredCategories.FirstOrDefault(c => c.Equals("Manga", StringComparison.OrdinalIgnoreCase))
        ?? configuredCategories[0];
}
