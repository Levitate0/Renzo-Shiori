namespace RenzoBackend.Models;

public class Chapter : ChapterDescriptorBase
{
    public string? Name { get; set; } = string.Empty;
    public decimal? Number
    {
        get => ChapterNumber;
        set => ChapterNumber = value;
    }
    public DateTime? ProviderUploadDate { get; set; }
    /// <summary>
    /// When the update scan first discovered this chapter in the library (UTC) —
    /// Suwayomi-style "found" time. Drives the Updates feed, independent of the
    /// source's publish date. Null for chapters recorded before this was tracked.
    /// </summary>
    public DateTime? DateFetched { get; set; }
    public string? Url { get; set; }
    public int ProviderIndex { get; set; }
    public DateTime? DownloadDate { get; set; }
    public bool ShouldDownload { get; set; }
    public bool IsDeleted { get; set; }
    /// <summary>
    /// A paid/coin-gated chapter the source's own extension doesn't list (parsed
    /// by LockedChapterSupplementService from the site's chapter page). Shown as
    /// "locked" until the user unlocks it on the site; downloads only succeed once
    /// their logged-in session actually owns it.
    /// </summary>
    public bool IsLocked { get; set; }
    /// <summary>
    /// When a download last failed on this chapter's paywall (UTC).
    ///
    /// Owning a site login for a source means its paid chapters ARE queued —
    /// the account may well have bought them. For the ones it has NOT bought
    /// that turned into a permanent loop: queue, fail "Log in via Webview and
    /// purchased this chapter to read", reschedule 30 minutes later, forever.
    /// Measured at 2,248 such failures in one day, 2,134 of them from a single
    /// source, and because a provider only gets ONE download slot that loop was
    /// what starved every other chapter behind it.
    ///
    /// Stamping the attempt lets the queue leave the chapter alone for a while
    /// instead of never retrying (a chapter bought later must still arrive) or
    /// retrying constantly. Cleared the moment anything shows it is no longer
    /// paid, so a purchase is not made to wait out the backoff.
    /// </summary>
    public DateTime? LockedCheckedAt { get; set; }
    public int? PageCount { get; set; }
    public string? Filename { get; set; }
    public List<string> Pages { get; set; } = [];
}