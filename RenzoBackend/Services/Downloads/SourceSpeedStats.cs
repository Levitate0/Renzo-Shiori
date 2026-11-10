using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RenzoBackend.Services.Downloads;

/// <summary>How a source has been doing at downloads lately.</summary>
public sealed class SourceSpeedDto
{
    /// <summary>Downloads started in the window.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    /// <summary>Downloads that completed.</summary>
    [JsonPropertyName("downloads")]
    public int Downloads { get; set; }

    /// <summary>Downloads / attempts, 0..1; null with no attempts.</summary>
    [JsonPropertyName("successRate")]
    public double? SuccessRate { get; set; }

    /// <summary>Median seconds per completed chapter; null with none completed.</summary>
    [JsonPropertyName("medianSeconds")]
    public double? MedianSeconds { get; set; }
}

/// <summary>
/// Per source: how many downloads were tried and completed over the last week, and
/// how long a completed chapter took. Source migration ranks the sources to move
/// to by it ("similar load speed"), and shows it.
///
/// Live downloads are recorded as they finish. A restart would start that over,
/// so the first read after a start also reads the week back out of the server's
/// own log (its "Starting download" / "Download Complete" lines, the same numbers
/// worked out by hand on 2026-11-04: MangaFire 63 s and 78%, MangaK 2 s and 97%).
/// </summary>
public sealed class SourceSpeedStats
{
    private static readonly TimeSpan Window = TimeSpan.FromDays(7);
    private const int MaxPerSource = 5000;

    private readonly record struct Outcome(DateTime At, double? Seconds);

    private readonly ConcurrentDictionary<string, List<Outcome>> _byProvider = new(StringComparer.Ordinal);
    private readonly string? _logsDir;
    private readonly ILogger<SourceSpeedStats> _logger;
    /// <summary>Log lines from here on are recorded live; reading them back too would count them twice.</summary>
    private readonly DateTime _startedLocal = DateTime.Now;
    /// <summary>The week read back from the log, started when the service is, waited for by readers.</summary>
    private readonly Task _seeded;

    public SourceSpeedStats(IConfiguration config, ILogger<SourceSpeedStats> logger)
    {
        _logger = logger;
        string? runtime = config["runtimeDirectory"];
        _logsDir = string.IsNullOrEmpty(runtime) ? null : Path.Combine(runtime, "logs");
        _seeded = Task.Run(SeedFromLogs);
    }

    internal SourceSpeedStats(ILogger<SourceSpeedStats> logger)
    {
        _logger = logger;
        _seeded = Task.CompletedTask;
    }

    /// <summary>A download from <paramref name="provider"/> ended; <paramref name="took"/> when it completed, null when not.</summary>
    public void Record(string? provider, TimeSpan? took, DateTime? at = null)
    {
        if (string.IsNullOrEmpty(provider))
            return;
        List<Outcome> list = _byProvider.GetOrAdd(provider, _ => []);
        lock (list)
        {
            list.Add(new Outcome(at ?? DateTime.UtcNow, took?.TotalSeconds));
            if (list.Count > MaxPerSource)
                list.RemoveRange(0, list.Count - MaxPerSource);
        }
    }

    public SourceSpeedDto For(string provider)
    {
        _seeded.Wait(TimeSpan.FromSeconds(30));
        var dto = new SourceSpeedDto();
        if (!_byProvider.TryGetValue(provider, out List<Outcome>? list))
            return dto;
        DateTime since = DateTime.UtcNow - Window;
        List<double> seconds;
        lock (list)
        {
            var recent = list.Where(o => o.At >= since).ToList();
            dto.Attempts = recent.Count;
            seconds = recent.Where(o => o.Seconds != null).Select(o => o.Seconds!.Value).OrderBy(s => s).ToList();
        }
        dto.Downloads = seconds.Count;
        dto.SuccessRate = dto.Attempts == 0 ? null : (double)dto.Downloads / dto.Attempts;
        dto.MedianSeconds = seconds.Count == 0 ? null : seconds[seconds.Count / 2];
        return dto;
    }

    /// <summary>When this source last completed a download in the past week; null when it has not.</summary>
    public DateTime? LastDelivered(string provider)
    {
        _seeded.Wait(TimeSpan.FromSeconds(30));
        if (!_byProvider.TryGetValue(provider, out List<Outcome>? list))
            return null;
        lock (list)
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (list[i].Seconds != null)
                    return list[i].At;
        }
        return null;
    }

    private static readonly Regex StartLine = new(
        @"^\[\w+ (\d\d):(\d\d):(\d\d) INF DownloCommandService\] Starting download for chapter (\S+) of series (.*) from provider (.*)\.\.\.$",
        RegexOptions.CultureInvariant);
    private static readonly Regex DoneLine = new(
        @"^\[\w+ (\d\d):(\d\d):(\d\d) INF DownloCommandService\] Download Complete for chapter (\S+) of series (.*) from provider (.*)\.\.\.$",
        RegexOptions.CultureInvariant);

    private void SeedFromLogs()
    {
        if (_logsDir == null || !Directory.Exists(_logsDir))
            return;
        // Read into a buffer, then put it in FRONT of what was recorded live
        // meanwhile: the lists stay oldest first, so trimming to the cap drops the
        // oldest outcomes, never the newest.
        var seed = new Dictionary<string, List<Outcome>>(StringComparer.Ordinal);
        void Add(string provider, TimeSpan? took, DateTime at)
        {
            if (!seed.TryGetValue(provider, out List<Outcome>? l))
                seed[provider] = l = [];
            // Log times are the server's local time; live outcomes are UTC.
            l.Add(new Outcome(at.ToUniversalTime(), took?.TotalSeconds));
        }
        try
        {
            DateTime from = DateTime.Now.Date - Window;
            int read = 0;
            foreach (string file in Directory.EnumerateFiles(_logsDir, "log-*.txt").OrderBy(f => f, StringComparer.Ordinal))
            {
                string stamp = Path.GetFileNameWithoutExtension(file)[4..];
                if (!DateTime.TryParseExact(stamp, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime day) || day < from)
                    continue;
                var open = new Dictionary<(string, string, string), DateTime>();
                using var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
                while (reader.ReadLine() is { } line)
                {
                    Match m = StartLine.Match(line);
                    if (m.Success)
                    {
                        DateTime at = At(day, m);
                        if (at >= _startedLocal)
                            break;
                        var k = (m.Groups[4].Value, m.Groups[5].Value.ToLowerInvariant(), m.Groups[6].Value);
                        if (open.TryGetValue(k, out DateTime earlier))
                            Add(k.Item3, null, earlier.ToUniversalTime()); // started again before completing: that attempt failed
                        open[k] = at;
                        continue;
                    }
                    m = DoneLine.Match(line);
                    if (!m.Success)
                        continue;
                    var key = (m.Groups[4].Value, m.Groups[5].Value.ToLowerInvariant(), m.Groups[6].Value);
                    DateTime done = At(day, m);
                    if (open.Remove(key, out DateTime started) && done >= started && done - started < TimeSpan.FromHours(1))
                    {
                        Add(key.Item3, done - started, done.ToUniversalTime());
                        read++;
                    }
                }
                // Attempts still open at the end of the day did not complete.
                foreach (var (k, at) in open)
                    Add(k.Item3, null, at.ToUniversalTime());
            }
            foreach (var (provider, outcomes) in seed)
            {
                outcomes.Sort((a, b) => a.At.CompareTo(b.At));
                List<Outcome> list = _byProvider.GetOrAdd(provider, _ => []);
                lock (list)
                {
                    list.InsertRange(0, outcomes);
                    if (list.Count > MaxPerSource)
                        list.RemoveRange(0, list.Count - MaxPerSource);
                }
            }
            _logger.LogInformation("Source download speeds: read {Count} completed download(s) from the last week's log.", read);
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "Could not read past download speeds from the log; only new downloads will count.");
        }
    }

    private static DateTime At(DateTime day, Match m) =>
        day.AddHours(int.Parse(m.Groups[1].Value)).AddMinutes(int.Parse(m.Groups[2].Value)).AddSeconds(int.Parse(m.Groups[3].Value));
}
