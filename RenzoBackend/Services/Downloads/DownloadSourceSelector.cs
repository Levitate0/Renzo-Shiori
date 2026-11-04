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
/// speculatively. A source that has just failed is remembered
/// (SourceHealthService) and ranks last until it is re-checked, so the next
/// chapter goes straight to the source that worked.
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
    /// <param name="steppedDown">
    /// Which sources just failed (SourceHealthService.IsSteppedDown). They rank
    /// after every source that has not, so a chapter goes to the source that
    /// works; null treats every source as healthy.
    /// </param>
    public static PreferredSourceLookup Prepare(SeriesEntity series, SeriesProviderEntity candidate, bool downloadAll = false,
        Func<SeriesProviderEntity, bool>? steppedDown = null) =>
        new(series, candidate, series.PrioritizeFreeChapters, downloadAll, steppedDown);

    /// <summary>
    /// The download order: per-series Priority, storage first on a tie, Id to
    /// make it total — with stepped-down sources moved behind the rest. The
    /// initial choice and the step-down walk both use it; change one, change all.
    /// </summary>
    public static IEnumerable<SeriesProviderEntity> Ordered(IEnumerable<SeriesProviderEntity> sources,
        Func<SeriesProviderEntity, bool>? steppedDown = null) =>
        sources
            .OrderBy(p => steppedDown != null && steppedDown(p))
            .ThenBy(p => p.Priority)
            .ThenByDescending(p => p.IsStorage)
            .ThenBy(p => p.Id);

    /// <summary>
    /// The source to hand chapter <paramref name="number"/> to when the one it is
    /// queued on cannot deliver: a step-down, or a move off a stalled queue. One
    /// rule for both; change one, change all.
    ///
    /// Sources that have not failed recently first, then a free copy before a
    /// paid one, then the usual download order. A paid copy is a candidate only
    /// when the owner has a site login for that source and the chapter was not
    /// already found paid there within the scan's paywall interval: handing a
    /// free chapter to a source where it is paid just marked it locked there and
    /// left it queued nowhere.
    /// </summary>
    /// <param name="excluded">The source it is on now, and any it already failed on.</param>
    /// <param name="allowSteppedDown">
    /// Whether a stepped-down source may be picked (ranked last). Yes at the end
    /// of the line — retries spent, a short listing, a disabled source — where the
    /// alternative is giving up; no for an early hand-off, where the alternative
    /// is simply retrying here.
    /// </param>
    /// <param name="loginProviders">Providers the series' owner has a working site login for.</param>
    public static SeriesProviderEntity? PickHandOff(IEnumerable<SeriesProviderEntity> sources, decimal number,
        Func<SeriesProviderEntity, bool> excluded, Func<SeriesProviderEntity, bool>? steppedDown, bool allowSteppedDown,
        IReadOnlySet<string> loginProviders, DateTime now)
    {
        DateTime paidRecently = now - DownloadsExtensions.PaywallRetryInterval;
        bool Paid(SeriesProviderEntity p) => HasChapterLocked(p, number);
        bool KnownPaidRecently(SeriesProviderEntity p) => p.Chapters.Any(c => !c.IsDeleted && c.Number == number
            && c.IsLocked && c.LockedCheckedAt > paidRecently);
        bool Down(SeriesProviderEntity p) => steppedDown != null && steppedDown(p);

        return sources
            .Where(p => !excluded(p) && CanDownloadFrom(p) && HasChapter(p, number)
                && (allowSteppedDown || !Down(p))
                && (!Paid(p) || (loginProviders.Contains(p.Provider) && !KnownPaidRecently(p))))
            .OrderBy(Down)
            .ThenBy(Paid)
            .ThenBy(p => p.Priority)
            .ThenByDescending(p => p.IsStorage)
            .ThenBy(p => p.Id)
            .FirstOrDefault();
    }

    /// <summary>Is this source's copy of the chapter paywalled?</summary>
    public static bool HasChapterLocked(SeriesProviderEntity p, decimal number) =>
        p.Chapters.Any(c => !c.IsDeleted && c.Number == number
            && (c.IsLocked || RenzoBackend.Extensions.ModelExtensions.IsLockedChapterName(c.Name)));

    /// <summary>
    /// The prepared answer to "should <c>candidate</c> download chapter N of this
    /// series?" — see <see cref="Prepare"/>.
    /// </summary>
    public sealed class PreferredSourceLookup
    {
        private readonly bool _always;
        private readonly bool _never;
        private readonly bool _preferFree;
        private readonly bool _downloadAll;
        private readonly decimal? _candidateCutoff;
        private readonly Guid _candidateId;
        private readonly HashSet<decimal> _candidateLocked = [];
        private readonly List<(Guid Id, HashSet<decimal> Numbers)> _ordered = [];

        internal PreferredSourceLookup(SeriesEntity series, SeriesProviderEntity candidate, bool preferFree, bool downloadAll,
            Func<SeriesProviderEntity, bool>? steppedDown = null)
        {
            _candidateId = candidate.Id;
            _preferFree = preferFree;
            _downloadAll = downloadAll;
            _candidateCutoff = candidate.ContinueAfterChapter;

            // A storage source is the on-disk copy, not a competitor for a download.
            // Unless it has just failed: a storage source that cannot deliver
            // (AllManga behind a captcha) claimed every chapter, failed each one
            // and handed it down, all over again on every scan. Stepped down, it
            // competes like any other source and ranks last.
            if (candidate.IsStorage && !(steppedDown?.Invoke(candidate) ?? false))
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

            foreach (SeriesProviderEntity p in Ordered(series.Sources
                .Where(p => p.Id == candidate.Id || CanDownloadFrom(p)), steppedDown))
            {
                // The candidate never needs its own set: it is offering the
                // chapter right now, which is what makes it a holder.
                // "Prioritize free chapters": a source only counts as HOLDING the
                // chapter when its copy is free. A paywalled copy therefore stops
                // blocking the sources below it, which is the whole point — the
                // chapter arrives from wherever it is actually readable instead of
                // waiting on the top source to unlock.
                // A source only HOLDS a chapter if it would actually download it.
                //
                // Each source carries its own ContinueAfterChapter cutoff, and the
                // download path drops anything at or below it. Counting such a
                // chapter as "held" made the top source claim it and then refuse
                // it, while every source below was told to stand down — so the
                // chapter was never downloaded by anyone. Real case: EZmanga at
                // priority 0 with a cutoff of 61 claimed chapters 59 and 60, which
                // MangaFire (cutoff 58) had and would have taken; both stayed
                // missing indefinitely, with no error anywhere.
                bool AboveCutoff(Models.Chapter c) =>
                    downloadAll || p.ContinueAfterChapter == null || c.Number > p.ContinueAfterChapter;

                bool Holds(Models.Chapter c) =>
                    !c.IsDeleted && c.Number.HasValue
                    && AboveCutoff(c)
                    && !(preferFree && (c.IsLocked
                        || RenzoBackend.Extensions.ModelExtensions.IsLockedChapterName(c.Name)));

                HashSet<decimal> numbers = p.Id == candidate.Id
                    ? []
                    : p.Chapters.Where(Holds).Select(c => c.Number!.Value).ToHashSet();
                _ordered.Add((p.Id, numbers));

                // The candidate's own paywalled chapters, so it can stand aside for
                // a free copy further down.
                if (preferFree && p.Id == candidate.Id)
                {
                    foreach (Models.Chapter c in p.Chapters)
                    {
                        if (!c.IsDeleted && c.Number.HasValue && (c.IsLocked
                            || RenzoBackend.Extensions.ModelExtensions.IsLockedChapterName(c.Name)))
                        {
                            _candidateLocked.Add(c.Number.Value);
                        }
                    }
                }
            }
        }

        /// <summary>True when this scan's source is the one that should download the chapter.</summary>
        /// <param name="number">The chapter number.</param>
        /// <param name="lockedHere">
        /// Whether the candidate's own copy is paywalled. Only consulted with
        /// "Prioritize free chapters" on, and only to let it stand aside.
        /// </param>
        public bool Keeps(decimal number, bool lockedHere = false)
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
                {
                    // Below this source's own cutoff: it is going to drop the
                    // chapter anyway, so it must not claim it. Standing aside is
                    // what lets a source further down actually fetch it.
                    if (!_downloadAll && _candidateCutoff != null && number <= _candidateCutoff)
                        continue;
                    // Paywalled here and "prefer free" is on: keep walking, and let
                    // a lower-priority source that has it free take it. If none
                    // does, the loop falls through and this source downloads it
                    // anyway — preferring free is not refusing to download.
                    if (_preferFree && (lockedHere || _candidateLocked.Contains(number)))
                        continue;
                    return true;
                }
                if (numbers.Contains(number))
                    return false;
            }
            return true;
        }
    }
}
