using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace RenzoWindows;

/// <summary>Progress for a single download broadcast (posted to the web UI as `renzo:download`).</summary>
public readonly record struct DownloadProgress(
    string State, string? SeriesId, string? ChapterKey, double ChapterNumber, int Done, int Total);

/// <summary>
/// Fully-native offline downloader for the desktop client — the counterpart to
/// Android's foreground service. Drains the persisted job queue on a background
/// task, fetches page images over HTTP (Bearer auth, valid for hours), writes
/// them via <see cref="RenzoStore"/>, and updates the manifest. Downloads keep
/// running while the window is minimized/hidden. Progress is raised on
/// <see cref="Progress"/> for the shell to forward to the web UI.
/// </summary>
public sealed class RenzoDownloader
{
    /// <summary>Concurrent page fetches per chapter (network is the bottleneck).</summary>
    private const int PageConcurrency = 5;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(40) };

    private readonly RenzoStore _store;
    private readonly object _gate = new();
    private bool _running;
    private volatile bool _stop;

    public event Action<DownloadProgress>? Progress;

    public RenzoDownloader(RenzoStore store) => _store = store;

    /// <summary>Kick the queue drainer. Safe to call repeatedly — one worker at a time.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_running) return;
            _running = true;
            _stop = false;
        }
        _ = Task.Run(RunQueueAsync);
    }

    public void Stop()
    {
        _stop = true;
        _store.ClearJobs();
    }

    private async Task RunQueueAsync()
    {
        try
        {
            while (!_stop)
            {
                string? job = _store.TakeJob();
                if (job == null) break;
                try { await DownloadJobAsync(job); }
                catch { /* skip a bad job, keep the queue moving */ }
            }
        }
        finally
        {
            lock (_gate) { _running = false; }
            Emit("idle", null, null, 0, 0, 0);
        }
    }

    private async Task DownloadJobAsync(string payload)
    {
        JsonObject job = JsonNode.Parse(payload)?.AsObject() ?? throw new FormatException("bad job");
        string baseUrl = (job["baseUrl"]?.GetValue<string>() ?? "").TrimEnd('/');
        string token = job["token"]?.GetValue<string>() ?? "";
        JsonObject series = job["series"]?.AsObject() ?? new JsonObject();
        string seriesId = series["seriesId"]?.GetValue<string>() ?? "";
        string title = series["title"]?.GetValue<string>() ?? "";

        await EnsureSeriesMetaAsync(series, baseUrl, token);

        JsonArray chapters = job["chapters"]?.AsArray() ?? new JsonArray();
        int total = chapters.Count;
        for (int i = 0; i < total; i++)
        {
            if (_stop) return;
            JsonObject ch = chapters[i]!.AsObject();
            string chapterKey = ch["chapterKey"]?.GetValue<string>() ?? "";
            double chapterNumber = ch["chapterNumber"]?.GetValue<double>() ?? 0;
            Emit("downloading", seriesId, chapterKey, chapterNumber, i, total);
            if (!_store.HasChapter(chapterKey))
            {
                try { await DownloadChapterAsync(ch, seriesId, title, baseUrl, token); }
                catch { /* skip a bad chapter, keep the trip going */ }
            }
            Emit("saved", seriesId, chapterKey, chapterNumber, i + 1, total);
        }
    }

    private async Task DownloadChapterAsync(JsonObject ch, string seriesId, string title, string baseUrl, string token)
    {
        string chapterKey = ch["chapterKey"]?.GetValue<string>() ?? "";
        double chapterNumber = ch["chapterNumber"]?.GetValue<double>() ?? 0;
        JsonArray pagePaths = ch["pagePaths"]?.AsArray() ?? new JsonArray();
        int n = pagePaths.Count;
        string dir = $"offline/{Sanitize(chapterKey)}";

        // Fetch pages in parallel (network-bound); results are placed by index so
        // page order is preserved regardless of completion order.
        var rels = new string?[n];
        var sizes = new long[n];
        using var sem = new SemaphoreSlim(Math.Min(PageConcurrency, Math.Max(1, n)));
        var tasks = new List<Task>(n);
        for (int p = 0; p < n; p++)
        {
            int idx = p;
            tasks.Add(Task.Run(async () =>
            {
                await sem.WaitAsync();
                try
                {
                    if (_stop) return;
                    (byte[] data, string? ct)? res = await HttpGetAsync(Resolve(baseUrl, pagePaths[idx]!.GetValue<string>()), token);
                    if (res == null) return;
                    string rel = $"{dir}/{idx.ToString().PadLeft(4, '0')}.{Ext(res.Value.ct)}";
                    _store.WriteFile(rel, res.Value.data);
                    rels[idx] = rel;
                    sizes[idx] = res.Value.data.LongLength;
                }
                finally { sem.Release(); }
            }));
        }
        await Task.WhenAll(tasks);

        var savedPaths = new JsonArray();
        long bytes = 0;
        for (int p = 0; p < n; p++)
        {
            if (rels[p] == null) continue;
            savedPaths.Add(rels[p]);
            bytes += sizes[p];
        }
        if (savedPaths.Count == 0) return;

        var entry = new JsonObject
        {
            ["seriesId"] = seriesId,
            ["chapterKey"] = chapterKey,
            ["chapterNumber"] = chapterNumber,
            ["seriesTitle"] = title,
            ["pageCount"] = savedPaths.Count,
            ["pagePaths"] = savedPaths,
            ["bytes"] = bytes,
            ["savedAt"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        _store.UpdateManifest(m => m["chapters"]!.AsObject()[chapterKey] = entry);
    }

    private async Task EnsureSeriesMetaAsync(JsonObject series, string baseUrl, string token)
    {
        string seriesId = series["seriesId"]?.GetValue<string>() ?? "";
        JsonObject existing = _store.GetManifest();
        string? existingCover = existing["series"]?.AsObject()[seriesId]?.AsObject()?["coverPath"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(existingCover)) return;

        string? coverPath = null;
        string coverSrc = series["coverPath"]?.GetValue<string>() ?? "";
        if (coverSrc.Length > 0)
        {
            (byte[] data, string? ct)? res = await HttpGetAsync(Resolve(baseUrl, coverSrc), token);
            if (res != null)
            {
                coverPath = $"offline/covers/{Sanitize(seriesId)}.{Ext(res.Value.ct)}";
                _store.WriteFile(coverPath, res.Value.data);
            }
        }

        var entry = new JsonObject
        {
            ["seriesId"] = seriesId,
            ["title"] = series["title"]?.GetValue<string>() ?? "",
            ["description"] = series["description"]?.GetValue<string>() ?? "",
            ["author"] = series["author"]?.GetValue<string>() ?? "",
        };
        if (coverPath != null) entry["coverPath"] = coverPath;
        _store.UpdateManifest(m => m["series"]!.AsObject()[seriesId] = entry);
    }

    private static string Resolve(string baseUrl, string path) =>
        path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : baseUrl + path;

    private static async Task<(byte[] data, string? ct)?> HttpGetAsync(string url, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using HttpResponseMessage resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            byte[] data = await resp.Content.ReadAsByteArrayAsync();
            return (data, resp.Content.Headers.ContentType?.MediaType);
        }
        catch
        {
            return null;
        }
    }

    private static string Ext(string? ct)
    {
        string c = ct?.ToLowerInvariant() ?? "";
        if (c.Contains("png")) return "png";
        if (c.Contains("webp")) return "webp";
        if (c.Contains("avif")) return "avif";
        if (c.Contains("gif")) return "gif";
        return "jpg";
    }

    private static string Sanitize(string key) => Regex.Replace(key, "[^a-zA-Z0-9._-]", "_");

    private void Emit(string state, string? seriesId, string? chapterKey, double chapterNumber, int done, int total) =>
        Progress?.Invoke(new DownloadProgress(state, seriesId, chapterKey, chapterNumber, done, total));
}
