using System.Collections.Concurrent;
using RenzoBackend.Models.Database;

namespace RenzoBackend.Services.Downloads;

/// <summary>Why a source call did not deliver, as far as stepping a source down goes.</summary>
public enum SourceFailureKind
{
    /// <summary>The site wants a person to solve a captcha. Site-wide: every series on the source is affected.</summary>
    Captcha,
    /// <summary>A source call ran past its time limit.</summary>
    Timeout,
    /// <summary>A waiting job sat in a source's queue far longer than that source takes to serve one.</summary>
    Backlog,
    /// <summary>Any other failure that is the source's own: an error, no pages, a dead image host.</summary>
    Error,
    /// <summary>Our own plumbing failed (the extension runner restarted mid-call). Says nothing about the source.</summary>
    Transport,
}

/// <summary>
/// Remembers which sources just failed, so the next chapter goes straight to the
/// source that works instead of proving the failure again.
///
/// Before this, nothing remembered anything. AllManga asked for a captcha on
/// every chapter of "Caught by the Villain" and every one of them was tried
/// there first: 832 attempts in a day, 0 downloads, about 8 seconds each before
/// the step-down, and the reader paid the same 9 seconds on every open.
///
/// A source that fails is "stepped down": downloads, step-downs and the reader
/// rank it after every source that has not failed, for a while. When the while
/// is up it is simply ranked normally again, and the next chapter that would use
/// it is the re-check. Success clears it; another failure steps it down again for
/// twice as long (30 min, 1 h, 2 h ... 24 h at most), so a source that stays
/// broken costs one attempt a day instead of one per chapter.
///
/// Per series, because a source can serve one series and fail another (a
/// removed title, one dead chapter host), except for a captcha, which is the
/// site's and steps the source down for every series.
///
/// shortcut: in memory only. A restart forgets every step-down, so each broken
/// source costs one more attempt per series after a restart. Persist it if
/// restarts become frequent.
/// </summary>
public sealed class SourceHealthService
{
    private static readonly TimeSpan FirstStepDown = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan LongestStepDown = TimeSpan.FromHours(24);

    /// <summary>
    /// Plain errors, on DIFFERENT chapters, needed in a row before a source
    /// steps down. One is not enough: a single bad upload or a dropped connection
    /// is not a broken source, and reading that one chapter again (a prefetch
    /// and an open of it overlapping, an append retry) is the same evidence, not
    /// more. Captcha, timeout and backlog step down at once.
    /// </summary>
    internal const int ErrorsBeforeStepDown = 2;

    /// <summary>
    /// After a start, the extension runner is cold (JVM warming up, every
    /// extension loading, Cloudflare clearances re-solved) and the first source
    /// calls can run into their limit. Four sources were stepped down for a
    /// timeout within three minutes of the 2026-11-04 04:28 restart, MangaFire
    /// among them. Meanwhile a timeout is only a plain error.
    /// </summary>
    internal static readonly TimeSpan StartupGrace = TimeSpan.FromMinutes(10);

    private sealed class State
    {
        public int Strikes;
        public int Errors;
        /// <summary>Chapters that have errored since the last success; a repeat is not counted again.</summary>
        public readonly HashSet<decimal> FailedChapters = [];
        public DateTime? Until;
        public string? Reason;
        /// <summary>Backlog only: the queue (provider) that stalled, and when, so any delivery from it ends the step-down.</summary>
        public string? Provider;
        public DateTime Since;
    }

    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _delivered = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Queue<DateTime>> _deliveries = new(StringComparer.Ordinal);
    private readonly ILogger<SourceHealthService> _logger;
    private readonly Func<DateTime> _now;
    private readonly DateTime _startedAt;

    public SourceHealthService(ILogger<SourceHealthService> logger) : this(logger, () => DateTime.UtcNow) { }

    internal SourceHealthService(ILogger<SourceHealthService> logger, Func<DateTime> now)
    {
        _logger = logger;
        _now = now;
        _startedAt = now();
    }

    /// <summary>A download from this source (its queue group, the provider name) just completed.</summary>
    public void NoteDelivered(string? provider)
    {
        if (string.IsNullOrEmpty(provider))
            return;
        DateTime now = _now();
        _delivered[provider] = now;
        Queue<DateTime> q = _deliveries.GetOrAdd(provider, _ => new Queue<DateTime>());
        lock (q)
        {
            q.Enqueue(now);
            while (q.Count > 0 && now - q.Peek() > DeliveryWindow)
                q.Dequeue();
        }
    }

    private static readonly TimeSpan DeliveryWindow = TimeSpan.FromHours(1);

    /// <summary>Downloads this source completed in the last hour: how much more it can take.</summary>
    public int DeliveredLastHour(string provider)
    {
        if (!_deliveries.TryGetValue(provider, out Queue<DateTime>? q))
            return 0;
        DateTime since = _now() - DeliveryWindow;
        lock (q)
            return q.Count(t => t > since);
    }

    /// <summary>
    /// When this source last completed a download; the service's start when it
    /// has not since, so a restart gives every source a fresh hour.
    /// </summary>
    public DateTime LastDelivered(string provider) =>
        _delivered.TryGetValue(provider, out DateTime at) ? at : _startedAt;

    private static string SeriesKey(Guid seriesProviderId) => "sp:" + seriesProviderId;

    /// <summary>
    /// A stalled download queue (SourceFailureKind.Backlog), kept apart from the
    /// source's health: it says nothing about reading from the source, so the
    /// reader neither ranks by it nor clears it; only a completed download does.
    /// </summary>
    private static string BacklogKey(Guid seriesProviderId) => "bl:" + seriesProviderId;

    /// <summary>The extension source, without the manga part ("pkg|sourceId").</summary>
    private static string? SiteKey(string? mihonProviderId) =>
        string.IsNullOrEmpty(mihonProviderId) ? null : "site:" + mihonProviderId;

    internal static TimeSpan StepDownFor(int strikes)
    {
        double minutes = FirstStepDown.TotalMinutes * Math.Pow(2, Math.Max(0, strikes - 1));
        return TimeSpan.FromMinutes(Math.Min(minutes, LongestStepDown.TotalMinutes));
    }

    /// <summary>Is this series' source stepped down for downloads right now (failed, or its queue stalled)?</summary>
    public bool IsSteppedDown(SeriesProviderEntity p) => IsSteppedDown(p.Id, p.MihonProviderId);

    public bool IsSteppedDown(Guid seriesProviderId, string? mihonProviderId) =>
        SteppedDownUntil(seriesProviderId, mihonProviderId) != null;

    /// <summary>Has this series' source failed recently? The reader's question: a stalled download queue does not count.</summary>
    public bool IsUnhealthy(SeriesProviderEntity p)
    {
        DateTime now = _now();
        return Active(SeriesKey(p.Id), now) != null || Active(SiteKey(p.MihonProviderId), now) != null;
    }

    /// <summary>When the latest step-down affecting this series' source ends; null when there is none.</summary>
    public DateTime? SteppedDownUntil(Guid seriesProviderId, string? mihonProviderId)
    {
        DateTime now = _now();
        DateTime? until = null;
        foreach (string? key in new[] { SeriesKey(seriesProviderId), SiteKey(mihonProviderId), BacklogKey(seriesProviderId) })
        {
            DateTime? u = Active(key, now);
            if (u != null && (until == null || u > until))
                until = u;
        }
        return until;
    }

    private DateTime? Active(string? key, DateTime now) =>
        key != null && _states.TryGetValue(key, out State? s) && s.Until > now && !DeliveredSince(s) ? s.Until : null;

    /// <summary>A backlog step-down whose queue has delivered since: the queue moves again, so it is over.</summary>
    private bool DeliveredSince(State s) =>
        s.Provider != null && _delivered.TryGetValue(s.Provider, out DateTime d) && d > s.Since;

    /// <summary>
    /// Records a failure. Returns true when the source is now stepped down for
    /// this series, so the caller can hand the chapter on at once.
    /// </summary>
    /// <param name="chapter">The chapter that failed, when there is one: an error on a chapter already counted is not counted again.</param>
    public bool ReportFailure(SeriesProviderEntity p, string? seriesTitle, SourceFailureKind kind, decimal? chapter = null) =>
        ReportFailure(p.Id, p.MihonProviderId, p.Provider, seriesTitle, kind, chapter);

    public bool ReportFailure(Guid seriesProviderId, string? mihonProviderId, string? provider, string? seriesTitle, SourceFailureKind kind,
        decimal? chapter = null)
    {
        if (kind == SourceFailureKind.Transport)
            return IsSteppedDown(seriesProviderId, mihonProviderId);
        if (kind == SourceFailureKind.Timeout && _now() - _startedAt < StartupGrace)
            kind = SourceFailureKind.Error;

        string? key = kind switch
        {
            SourceFailureKind.Captcha => SiteKey(mihonProviderId),
            SourceFailureKind.Backlog => BacklogKey(seriesProviderId),
            _ => SeriesKey(seriesProviderId),
        };
        key ??= SeriesKey(seriesProviderId);
        DateTime now = _now();
        State s = _states.GetOrAdd(key, _ => new State());
        lock (s)
        {
            // The queue delivered since its last stall: that one is over, and so
            // is the escalation (only a stall in a row doubles the wait).
            if (kind == SourceFailureKind.Backlog && DeliveredSince(s))
            {
                s.Strikes = 0;
                s.Until = null;
            }
            // Already stepped down: a job that was queued before the step-down
            // failing too is not news, and must not double the wait.
            if (s.Until > now)
                return true;
            // A captcha or a stalled queue is already counted elsewhere for this
            // series: say so without a second record.
            if (kind != SourceFailureKind.Captcha && kind != SourceFailureKind.Backlog
                && Active(SiteKey(mihonProviderId), now) != null)
                return true;
            if (kind == SourceFailureKind.Error)
            {
                // The same chapter failing again proves nothing new, before a
                // step-down or after one: re-reading the one broken upload after
                // every wait would otherwise double the step-down up to a day.
                if (chapter is decimal n && !s.FailedChapters.Add(n))
                    return false;
                // A re-check that fails (Strikes > 0, never cleared by a success)
                // steps straight back down; the run of errors is only asked of a
                // source that has not failed before.
                if (s.Strikes == 0 && ++s.Errors < ErrorsBeforeStepDown)
                    return false;
            }
            s.Errors = 0;
            s.Strikes++;
            s.Until = now + StepDownFor(s.Strikes);
            if (kind == SourceFailureKind.Backlog)
            {
                s.Provider = provider;
                s.Since = now;
            }
            s.Reason = kind switch
            {
                SourceFailureKind.Captcha => "asks for a captcha",
                SourceFailureKind.Timeout => "timed out",
                SourceFailureKind.Backlog => "backed up",
                _ => $"failed {ErrorsBeforeStepDown} times in a row",
            };
            _logger.LogWarning(
                "Stepped {Provider} down {Scope} until {Until:yyyy-MM-dd HH:mm} UTC ({Reason}, step-down #{Strikes}); other sources take its chapters, and it is re-checked then.",
                provider, kind == SourceFailureKind.Captcha ? "for every series" : $"for '{seriesTitle}'", s.Until, s.Reason, s.Strikes);
            return true;
        }
    }

    /// <summary>The source delivered: forget its failures, for this series and site-wide.</summary>
    /// <param name="downloaded">A download completed, so its queue is moving too: also clears a backlog step-down.</param>
    public void ReportSuccess(SeriesProviderEntity p, string? seriesTitle, bool downloaded = false) =>
        ReportSuccess(p.Id, p.MihonProviderId, p.Provider, seriesTitle, downloaded);

    public void ReportSuccess(Guid seriesProviderId, string? mihonProviderId, string? provider, string? seriesTitle, bool downloaded = false)
    {
        foreach (string? key in new[] { SeriesKey(seriesProviderId), SiteKey(mihonProviderId), downloaded ? BacklogKey(seriesProviderId) : null })
        {
            if (key == null || !_states.TryRemove(key, out State? s))
                continue;
            if (s.Strikes > 0)
                _logger.LogInformation("{Provider} works again ('{Series}' got pages from it); it is back in its normal place.", provider, seriesTitle);
        }
    }

    /// <summary>How a swallowed source exception counts.</summary>
    public static SourceFailureKind Classify(Exception? e)
    {
        if (e == null)
            return SourceFailureKind.Error;
        if (e is TimeoutException)
            return SourceFailureKind.Timeout;
        string m = e.Message ?? string.Empty;
        if (m.Contains("captcha", StringComparison.OrdinalIgnoreCase))
            return SourceFailureKind.Captcha;
        // The sidecar is reached over HTTP on localhost, so a transport-level
        // HttpRequestException is our own connection to it dropping (a restart
        // for leaked browser helpers), never the site: a site's own HTTP status
        // comes back as a SidecarException. The same restart disposes the
        // interop ("not loaded", ObjectDisposedException) for calls in flight.
        if (e is HttpRequestException || e is ObjectDisposedException || e is OperationCanceledException
            || m.Contains(" not loaded", StringComparison.Ordinal))
            return SourceFailureKind.Transport;
        // The extension runner's only loopback peers are our own Cloudflare
        // solvers. One of them being down ("Failed to connect to /127.0.0.1:8191",
        // 846 times on 2026-11-03, nearly all Comic Asura images) or dying
        // mid-solve is our plumbing too; no site lives on loopback.
        if (m.Contains("Failed to connect to /127.0.0.1:", StringComparison.Ordinal)
            || m.Contains("on http://127.0.0.1:", StringComparison.Ordinal))
            return SourceFailureKind.Transport;
        // Our own overlapping calls into one extension instance, not the site.
        if (m.Contains("must not be called concurrently", StringComparison.OrdinalIgnoreCase))
            return SourceFailureKind.Transport;
        // The extension runner turned the call away because that site already
        // holds its share of workers (SidecarServer's per-site cap).
        if (m.Contains("sidecar site slots full", StringComparison.Ordinal))
            return SourceFailureKind.Transport;
        return SourceFailureKind.Error;
    }
}
