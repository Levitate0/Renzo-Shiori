using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Extensions;

namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>Metadata for one source returned by the sidecar's /sources/load.</summary>
    public sealed class SidecarSourceMeta
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Lang { get; set; } = "";
        public bool SupportsLatest { get; set; }
        public bool IsConfigurable { get; set; }
        public bool IsHttp { get; set; }
        public string BaseUrl { get; set; } = "";
        public int VersionId { get; set; }
    }

    /// <summary>
    /// Typed HTTP client for the JVM sidecar (127.0.0.1). Translates the sidecar's JSON into the
    /// existing bridge DTOs (Manga/Chapter/Page/…) so <see cref="SidecarSourceInterop"/> can back
    /// <c>ISourceInterop</c> without the app noticing the transport changed.
    /// </summary>
    public sealed class SidecarClient
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly HttpClient _http;

        public SidecarClient(HttpClient http) => _http = http;

        // ---- lifecycle / extension management ----

        public async Task<bool> HealthAsync(CancellationToken token = default)
        {
            try
            {
                using var r = await _http.GetAsync("/health", token).ConfigureAwait(false);
                return r.IsSuccessStatusCode;
            }
            catch { return false; }
        }

        public Task SetupAsync(string dataRoot, string tempRoot, CancellationToken token = default) =>
            PostAsync("/setup", new { dataRoot, tempRoot }, token);

        public Task ConvertAsync(string apkPath, string jarPath, CancellationToken token = default) =>
            PostAsync("/convert", new { apkPath, jarPath }, token);

        /// <summary>Push the app's network settings (FlareSolverr/Cloudflare, SOCKS proxy) to the engine.</summary>
        public Task ConfigAsync(object settings, CancellationToken token = default) =>
            PostAsync("/config", settings, token);

        /// <summary>Inject site-login cookies into the engine's shared jar. Returns the number added.</summary>
        public async Task<int> InjectCookiesAsync(IEnumerable<SidecarCookie> cookies, CancellationToken token = default)
        {
            using var doc = await PostJsonAsync("/cookies/inject", new { cookies }, token).ConfigureAwait(false);
            return Int(doc.RootElement, "added");
        }

        /// <summary>Snapshot the cookies currently in the engine's jar for a host.</summary>
        public async Task<List<SidecarCookie>> SnapshotCookiesAsync(string host, CancellationToken token = default)
        {
            using var doc = await PostJsonAsync("/cookies/snapshot", new { host }, token).ConfigureAwait(false);
            var list = new List<SidecarCookie>();
            if (doc.RootElement.TryGetProperty("cookies", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var c in arr.EnumerateArray())
                    list.Add(new SidecarCookie(Str(c, "name"), Str(c, "value"), Str(c, "domain"), Str(c, "path"), Bool(c, "secure")));
            return list;
        }

        /// <summary>Remove every cookie for a host from the engine's jar (log out / delete).</summary>
        public Task ClearCookiesAsync(string host, CancellationToken token = default) =>
            PostAsync("/cookies/clear", new { host }, token);

        public async Task<List<SidecarSourceMeta>> LoadSourcesAsync(string jarPath, string className, CancellationToken token = default)
        {
            using var doc = await PostJsonAsync("/sources/load", new { jarPath, className }, token).ConfigureAwait(false);
            var list = new List<SidecarSourceMeta>();
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                list.Add(new SidecarSourceMeta
                {
                    Id = e.GetProperty("id").GetInt64(),
                    Name = Str(e, "name"),
                    Lang = Str(e, "lang"),
                    SupportsLatest = Bool(e, "supportsLatest"),
                    IsConfigurable = Bool(e, "isConfigurable"),
                    IsHttp = Bool(e, "isHttp"),
                    BaseUrl = Str(e, "baseUrl"),
                    VersionId = Int(e, "versionId"),
                });
            }
            return list;
        }

        public Task UnloadAsync(string jarPath, CancellationToken token = default) =>
            PostAsync("/source/unload", new { jarPath }, token);

        // ---- source operations ----

        public Task<MangaList> PopularAsync(long id, int page, CancellationToken token = default) =>
            MangaListAsync("/source/popular", new { id = id.ToString(), page }, token);

        public Task<MangaList> LatestAsync(long id, int page, CancellationToken token = default) =>
            MangaListAsync("/source/latest", new { id = id.ToString(), page }, token);

        public Task<MangaList> SearchAsync(long id, int page, string query, CancellationToken token = default) =>
            MangaListAsync("/source/search", new { id = id.ToString(), page, query }, token);

        public async Task<ParsedManga> DetailsAsync(long id, Manga manga, CancellationToken token = default)
        {
            using var doc = await PostJsonAsync("/source/details", new { id = id.ToString(), manga = MangaToNode(manga) }, token).ConfigureAwait(false);
            var m = ReadManga<ParsedManga>(doc.RootElement.GetProperty("manga"));
            if (doc.RootElement.TryGetProperty("realUrl", out var ru) && ru.ValueKind == JsonValueKind.String)
                m.RealUrl = ru.GetString()!;
            return m;
        }

        public async Task<List<ParsedChapter>> ChaptersAsync(long id, Manga manga, CancellationToken token = default)
        {
            using var doc = await PostJsonAsync("/source/chapters", new { id = id.ToString(), manga = MangaToNode(manga) }, token).ConfigureAwait(false);
            var list = new List<ParsedChapter>();
            int idx = 0;
            foreach (var e in doc.RootElement.EnumerateArray())
                list.Add(ReadChapter(e, idx++, manga.Title ?? string.Empty));
            return list;
        }

        public async Task<List<Page>> PagesAsync(long id, Chapter chapter, CancellationToken token = default)
        {
            using var doc = await PostJsonAsync("/source/pages", new { id = id.ToString(), chapter = ChapterToNode(chapter) }, token).ConfigureAwait(false);
            var list = new List<Page>();
            foreach (var e in doc.RootElement.EnumerateArray())
                list.Add(ReadPage(e));
            return list;
        }

        public async Task<ContentTypeStream> ImageAsync(long id, Page page, CancellationToken token = default)
        {
            var body = new { id = id.ToString(), page = PageToNode(page) };
            using var req = new HttpRequestMessage(HttpMethod.Post, "/source/image")
            {
                Content = JsonContent.Create(body),
            };
            // _http.Timeout does NOT bound this call: HttpClient's Timeout is a
            // CancelAfter on a CTS that SendAsync disposes as soon as it returns,
            // and ResponseHeadersRead makes SendAsync return as soon as headers
            // arrive — before the body below is ever read. Without its own
            // deadline, a sidecar that stalls mid-response (GC thrash, a wedged
            // JVM worker thread) leaves this await blocked forever with no
            // exception, silently leaking the caller's concurrency slot. Give
            // the whole header+body exchange the same budget _http.Timeout would
            // have provided if it actually covered the body read.
            using var bodyTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            bodyTimeout.CancelAfter(_http.Timeout);
            CancellationToken bodyToken = bodyTimeout.Token;

            var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, bodyToken).ConfigureAwait(false);
            var ct = resp.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            if (!resp.IsSuccessStatusCode || ct.Contains("application/json"))
            {
                var err = await resp.Content.ReadAsStringAsync(bodyToken).ConfigureAwait(false);
                resp.Dispose();
                throw new SidecarException($"image failed: {err}");
            }
            var bytes = await resp.Content.ReadAsByteArrayAsync(bodyToken).ConfigureAwait(false);
            resp.Dispose();
            return new SidecarContentTypeStream(bytes, ct);
        }

        // ---- JSON mapping ----

        private async Task<MangaList> MangaListAsync(string path, object body, CancellationToken token)
        {
            using var doc = await PostJsonAsync(path, body, token).ConfigureAwait(false);
            var root = doc.RootElement;
            var ml = new MangaList { HasNextPage = Bool(root, "hasNextPage"), Mangas = new List<ParsedManga>() };
            if (root.TryGetProperty("mangas", out var arr))
                foreach (var e in arr.EnumerateArray())
                    ml.Mangas.Add(ReadManga<ParsedManga>(e));
            return ml;
        }

        private static T ReadManga<T>(JsonElement e) where T : Manga, new()
        {
            var m = new T
            {
                Url = Str(e, "url"),
                Title = Str(e, "title"),
                Artist = StrOrNull(e, "artist"),
                Author = StrOrNull(e, "author"),
                Description = StrOrNull(e, "description"),
                Genre = StrOrNull(e, "genre"),
                Status = (Status)Int(e, "status"),
                ThumbnailUrl = StrOrNull(e, "thumbnail_url"),
                Initialized = Bool(e, "initialized"),
                Memo = RawOrNull(e, "memo"),
            };
            return m;
        }

        private static ParsedChapter ReadChapter(JsonElement e, int index, string mangaTitle)
        {
            long ms = e.TryGetProperty("date_upload", out var d) && d.ValueKind == JsonValueKind.Number ? d.GetInt64() : 0;
            string name = Str(e, "name");
            float chapterNumber = e.TryGetProperty("chapter_number", out var cn) && cn.ValueKind == JsonValueKind.Number ? (float)cn.GetDouble() : -1f;
            var chapter = new ParsedChapter
            {
                Url = Str(e, "url"),
                RealUrl = Str(e, "realUrl"),
                Name = name,
                DateUpload = DateTimeOffset.FromUnixTimeMilliseconds(ms),
                ChapterNumber = chapterNumber,
                Scanlator = StrOrNull(e, "scanlator"),
                Memo = RawOrNull(e, "memo"),
                Index = index,
            };
            // Parity with the IKVM ToParsedChapters path: the stored Chapter takes its Number/Name from
            // ParsedNumber/ParsedName (ModelExtensions.ToChapter). Without this every sidecar chapter
            // stored as Number 0 / empty name, collapsing the whole list to one entry — so every refresh
            // re-saw them all as "new" (an endless churn that also clobbered read state).
            chapter.ParsedNumber = Utilities.ChapterUtils.ParseChapterNumber(mangaTitle, name, chapterNumber);
            chapter.ParsedName = Utilities.ChapterUtils.Sanitize(name, mangaTitle);
            return chapter;
        }

        private static Page ReadPage(JsonElement e) => new()
        {
            Index = Int(e, "index"),
            Url = Str(e, "url"),
            ImageUrl = StrOrNull(e, "imageUrl"),
        };

        // Manga/Chapter/Page -> a JSON node the sidecar accepts (mirrors its field names + memo blob).
        private static Dictionary<string, object?> MangaToNode(Manga m)
        {
            var n = new Dictionary<string, object?>
            {
                ["url"] = m.Url, ["title"] = m.Title, ["artist"] = m.Artist, ["author"] = m.Author,
                ["description"] = m.Description, ["genre"] = m.Genre, ["status"] = (int)m.Status,
                ["thumbnail_url"] = m.ThumbnailUrl,
            };
            AttachMemo(n, m.Memo);
            return n;
        }

        private static Dictionary<string, object?> ChapterToNode(Chapter c)
        {
            var n = new Dictionary<string, object?>
            {
                ["url"] = c.Url, ["name"] = c.Name, ["scanlator"] = c.Scanlator,
            };
            AttachMemo(n, c.Memo);
            return n;
        }

        private static Dictionary<string, object?> PageToNode(Page p) => new()
        {
            ["index"] = p.Index, ["url"] = p.Url, ["imageUrl"] = p.ImageUrl,
        };

        private static void AttachMemo(Dictionary<string, object?> n, string? memo)
        {
            if (string.IsNullOrEmpty(memo)) return;
            try { n["memo"] = JsonSerializer.Deserialize<JsonElement>(memo); } catch { /* skip malformed */ }
        }

        // ---- HTTP plumbing ----

        private async Task PostAsync(string path, object body, CancellationToken token)
        {
            using var doc = await PostJsonAsync(path, body, token).ConfigureAwait(false);
        }

        private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken token)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
            using var resp = await _http.SendAsync(req, token).ConfigureAwait(false);
            var text = await resp.Content.ReadAsStringAsync(token).ConfigureAwait(false);
            var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
            if (!resp.IsSuccessStatusCode || (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var err)))
            {
                var msg = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var e2) ? e2.GetString() : text;
                var stack = doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("stack", out var st) ? st.GetString() : null;
                doc.Dispose();
                throw new SidecarException($"{path} failed: {msg}", stack);
            }
            return doc;
        }

        private static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        private static string? StrOrNull(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static string? RawOrNull(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Object ? v.GetRawText() : null;
        private static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        private static bool Bool(JsonElement e, string p) => e.TryGetProperty(p, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();
    }

    /// <summary>A cookie exchanged with the sidecar jar (name/value scoped to a domain+path).</summary>
    public sealed record SidecarCookie(string Name, string Value, string Domain, string Path = "/", bool Secure = true);

    public sealed class SidecarException : Exception
    {
        public string? Stack { get; }
        public SidecarException(string message, string? stack = null) : base(message) => Stack = stack;
    }

    internal sealed class SidecarContentTypeStream : ContentTypeStream
    {
        public override string ContentType { get; init; }
        public SidecarContentTypeStream(byte[] bytes, string contentType) : base(bytes) => ContentType = contentType;
    }
}
