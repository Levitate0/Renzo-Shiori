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
    public int? PageCount { get; set; }
    public string? Filename { get; set; }
    public List<string> Pages { get; set; } = [];
}