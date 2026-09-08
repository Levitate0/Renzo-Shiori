using System.Collections.Generic;
using System.Linq;
using RenzoBackend.Models.Database;

namespace RenzoBackend.Services.Downloads;

/// <summary>
/// Which single source a chapter should be downloaded from.
///
/// The rule is: among the sources that are usable and are known to hold the
/// chapter, the one with the best (lowest) per-series Priority wins, and the
/// others do not queue it at all.
///
/// This replaces "whichever source enqueued first wins". The queue key is
/// series+chapter-number, so a second source's attempt was silently dropped —
/// which meant the source that happened to scan first decided the download,
/// and the per-series priority order the user had set was ignored for new
/// chapters. It only ever applied afterwards, via the re-download pass, so a
/// chapter arrived from the wrong source and was then fetched a second time
/// from the right one.
///
/// Deciding here rather than at enqueue time keeps it race-free: every source's
/// scan asks the same question about the same data and only one of them can
/// answer "me", so there is nothing to collapse, replace, or roll back.
/// </summary>
public static class DownloadSourceSelector
{
    /// <summary>
    /// A source we can actually download from: installed, enabled, identified,
    /// and not a local/unknown placeholder.
    /// </summary>
    public static bool CanDownloadFrom(SeriesProviderEntity p) =>
        !p.IsUnknown && !p.IsLocal && !p.IsDisabled && !p.IsUninstalled
        && !string.IsNullOrEmpty(p.MihonProviderId);

    /// <summary>Does this source hold (know about) the chapter?</summary>
    public static bool HasChapter(SeriesProviderEntity p, decimal number) =>
        p.Chapters.Any(c => !c.IsDeleted && c.Number == number);

    /// <summary>
    /// Currently fetching successfully. ConsecutiveErrorCount is reset to 0 on
    /// every successful fetch, so this self-heals rather than needing a timeout.
    /// </summary>
    public static bool IsHealthy(SeriesProviderEntity p) => p.ConsecutiveErrorCount == 0;

    /// <summary>
    /// True when <paramref name="candidate"/> is the source that should download
    /// <paramref name="number"/> for this series.
    /// </summary>
    public static bool IsPreferredDownloadSource(SeriesEntity series, SeriesProviderEntity candidate, decimal number)
    {
        // A storage source is the on-disk copy, not a competitor for a download.
        if (candidate.IsStorage)
            return true;

        // Disabled or uninstalled sources do not download, full stop — the
        // caller may still be scanning one to keep its chapter list current.
        if (!CanDownloadFrom(candidate))
            return false;

        // The candidate is offering this chapter RIGHT NOW, so it counts as a
        // holder even though its own Chapters row may not exist yet — the scan
        // that discovered it has not been persisted. Without this the candidate
        // could exclude itself, every other source could be unaware of the
        // chapter, and nobody would ever download it.
        //
        // A higher-priority source only displaces the candidate if it is
        // HEALTHY. One that is erroring cannot be relied on to fetch anything,
        // and letting it win would park the chapter behind a broken source
        // indefinitely — the queue has no step-down fallback to rescue it.
        List<SeriesProviderEntity> contenders = series.Sources
            .Where(p => p.Id == candidate.Id
                || (CanDownloadFrom(p) && IsHealthy(p) && HasChapter(p, number)))
            .ToList();

        SeriesProviderEntity? best = contenders
            .OrderBy(p => p.Priority)
            .ThenByDescending(p => p.IsStorage)
            .ThenBy(p => p.Id)   // stable, so every source's scan agrees
            .FirstOrDefault();

        return best == null || best.Id == candidate.Id;
    }
}
