namespace RensaioBackend.Services.Downloads;

/// <summary>
/// Global ceiling on how much page-image data may be held in memory at once
/// across every concurrent chapter download.
///
/// Page images are buffered in memory between being fetched and being written
/// into the archive. Once pages are fetched in parallel, the worst case is
/// (chapters in flight) x (pages in parallel per chapter) x (page size), which
/// with permissive settings can reach many GB — enough to OOM the container.
/// This caps the total: a page fetch may not START until a slot is free, so the
/// downloader self-throttles instead of running the box out of memory.
///
/// Slots are counted rather than bytes (the size isn't known until the image has
/// already been downloaded), using a conservative per-page estimate.
/// </summary>
public static class PageMemoryBudget
{
    /// <summary>
    /// Assumed worst-case page size. Real pages on this instance run ~1-4 MB;
    /// 8 MB leaves headroom for oversized webtoon strips so the budget is a
    /// ceiling in practice, not an average that can be blown past.
    /// </summary>
    private const int AssumedPageMegabytes = 8;

    private const int DefaultBudgetMegabytes = 2048;

    private static readonly object _lock = new();
    private static int _budgetMegabytes = DefaultBudgetMegabytes;
    private static SemaphoreSlim _slots = new(SlotsFor(DefaultBudgetMegabytes));

    private static int SlotsFor(int budgetMegabytes) =>
        Math.Max(1, budgetMegabytes / AssumedPageMegabytes);

    /// <summary>
    /// The semaphore in force right now. Callers snapshot this once per chapter
    /// and release back to the SAME instance, so a settings change mid-download
    /// can't corrupt the count of an already-running chapter.
    /// </summary>
    public static SemaphoreSlim Current
    {
        get { lock (_lock) { return _slots; } }
    }

    public static int BudgetMegabytes
    {
        get { lock (_lock) { return _budgetMegabytes; } }
    }

    /// <summary>Applied live from settings; a no-op when the value is unchanged.</summary>
    public static void Configure(int budgetMegabytes)
    {
        if (budgetMegabytes <= 0)
            budgetMegabytes = DefaultBudgetMegabytes;
        lock (_lock)
        {
            if (budgetMegabytes == _budgetMegabytes)
                return;
            _budgetMegabytes = budgetMegabytes;
            // Downloads already running keep using the old semaphore they
            // captured; new chapters pick this one up.
            _slots = new SemaphoreSlim(SlotsFor(budgetMegabytes));
        }
    }
}
