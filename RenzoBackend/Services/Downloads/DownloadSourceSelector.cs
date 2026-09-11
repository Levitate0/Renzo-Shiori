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
///
/// Exactly one source is chosen, and it is simply the best enabled one that has
/// the chapter. Failure is not anticipated here — if that source cannot deliver
/// (missing pages, a dead CDN, a bad upload) the chapter steps down to the next
/// enabled source once its retries are spent, in
/// <c>DownloadCommandService.TryStepDownToNextSourceAsync</c>. The extra
/// download is then paid for once, on demand, instead of every source racing
/// speculatively.
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
    /// Precomputes everything the per-chapter decision needs, ONCE per scan.
    ///
    /// The obvious shape — ask per chapter, scanning every source's chapter list
    /// each time — is quadratic, and with "Download all chapters" enabled the
    /// input is not "the new chapters" but EVERY chapter of every series. On a
    /// library of a few thousand series that is billions of decimal comparisons
    /// and a list allocation per chapter, per sweep. Building the lookup once
    /// turns each decision into a walk of the sources with O(1) set probes.
    /// </summary>
    public static PreferredSourceLookup Prepare(SeriesEntity series, SeriesProviderEntity candidate) =>
        new(series, candidate);

    /// <summary>
    /// The prepared answer to "should <c>candidate</c> download chapter N of this
    /// series?" — see <see cref="Prepare"/>.
    /// </summary>
    public sealed class PreferredSourceLookup
    {
        private readonly bool _always;
        private readonly bool _never;
        private readonly Guid _candidateId;
        private readonly List<(Guid Id, HashSet<decimal> Numbers)> _ordered = [];

        internal PreferredSourceLookup(SeriesEntity series, SeriesProviderEntity candidate)
        {
            _candidateId = candidate.Id;

            // A storage source is the on-disk copy, not a competitor for a download.
            if (candidate.IsStorage)
            {
                _always = true;
                return;
            }

            // Disabled or uninstalled sources do not download, full stop — the
            // caller may still be scanning one to keep its chapter list current.
            if (!CanDownloadFrom(candidate))
            {
                _never = true;
                return;
            }

            foreach (SeriesProviderEntity p in series.Sources
                .Where(p => p.Id == candidate.Id || CanDownloadFrom(p))
                .OrderBy(p => p.Priority)
                .ThenByDescending(p => p.IsStorage)
                .ThenBy(p => p.Id))
            {
                // The candidate never needs its own set: it is offering the
                // chapter right now, which is what makes it a holder.
                HashSet<decimal> numbers = p.Id == candidate.Id
                    ? []
                    : p.Chapters
                        .Where(c => !c.IsDeleted && c.Number.HasValue)
                        .Select(c => c.Number!.Value)
                        .ToHashSet();
                _ordered.Add((p.Id, numbers));
            }
        }

        /// <summary>True when this scan's source is the one that should download the chapter.</summary>
        public bool Keeps(decimal number)
        {
            if (_always) return true;
            if (_never) return false;

            // The list is in the winning order, so the FIRST source that either is
            // the candidate or holds the chapter is the winner outright.
            //
            // The candidate is treated as a holder even though its own Chapters row
            // may not exist yet — the scan that discovered this chapter has not been
            // persisted. Without that it could exclude itself, every other source
            // could be unaware of the chapter, and nobody would download it.
            foreach ((Guid id, HashSet<decimal> numbers) in _ordered)
            {
                if (id == _candidateId)
                    return true;
                if (numbers.Contains(number))
                    return false;
            }
            return true;
        }
    }
}
