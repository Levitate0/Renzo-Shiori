using System.Collections.Concurrent;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;

namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>
    /// <see cref="ISourceInterop"/> backed by the JVM sidecar. Every call becomes a local HTTP
    /// request to the sidecar, which runs the actual Mihon extension on a real JVM. The app's
    /// business logic (locked chapters, site auth, reader, …) calls this interface exactly as it
    /// called the IKVM-backed <c>SourceInterop</c>, so nothing above the bridge changes.
    /// </summary>
    public sealed class SidecarSourceInterop : ISourceInterop
    {
        private readonly SidecarClient _client;
        private readonly SidecarSourceMeta _meta;

        public SidecarSourceInterop(SidecarClient client, SidecarSourceMeta meta)
        {
            _client = client;
            _meta = meta;
        }

        public long Id => _meta.Id;
        public string Name => _meta.Name;
        public string Language => _meta.Lang;
        public string BaseUrl => _meta.BaseUrl;
        public int VersionId => _meta.VersionId;
        public bool SupportsLatest => _meta.SupportsLatest;
        public bool IsConfigurableSource => _meta.IsConfigurable;
        public bool IsHttpSource => _meta.IsHttp;
        public bool IsCatalogueSource => true;
        public bool IsParsedHttpSource => false;

        public Task<MangaList> GetPopularAsync(int page, CancellationToken token = default) => _client.PopularAsync(_meta.Id, page, token);
        public Task<MangaList> GetLatestAsync(int page, CancellationToken token = default) => _client.LatestAsync(_meta.Id, page, token);
        public Task<MangaList> SearchAsync(int page, string query, CancellationToken token = default) => _client.SearchAsync(_meta.Id, page, query, token);
        public Task<ParsedManga> GetDetailsAsync(Manga manga, CancellationToken token = default) => _client.DetailsAsync(_meta.Id, manga, token);
        public Task<List<ParsedChapter>> GetChaptersAsync(Manga manga, CancellationToken token = default) => _client.ChaptersAsync(_meta.Id, manga, token);
        public Task<List<Page>> GetPagesAsync(Chapter chapter, CancellationToken token = default) => _client.PagesAsync(_meta.Id, chapter, token);
        public Task<ContentTypeStream> GetPageImageAsync(Page page, CancellationToken token = default) =>
            ThrottledImageAsync(page, token);

        // Image fetches per HOST allowed INTO the sidecar at once: exactly the
        // sidecar's own OkHttp maxRequestsPerHost (the MaxRequestsPerHost
        // setting), which is the real limit underneath — extensions fetch
        // images with Call.await(), i.e. enqueue().
        private static int MaxImageFetchesPerHost() => SidecarNetworkSettings.MaxRequestsPerHost;

        // Static so the limit survives interop rebuilds after a sidecar recycle —
        // a per-instance gate would hand a fresh allowance to a host whose old
        // fetches are still running. Keyed per image host, like OkHttp (see
        // SourceImageGate); "source:{id}" when the URL is only resolved inside
        // the sidecar, where the host can't be known up front.
        private static readonly ConcurrentDictionary<string, SourceImageGate> ImageSlots = new(StringComparer.OrdinalIgnoreCase);

        private string GateKey(Page page)
        {
            foreach (string? candidate in new[] { page.ImageUrl, page.Url })
            {
                if (!string.IsNullOrEmpty(candidate)
                    && Uri.TryCreate(candidate, UriKind.Absolute, out Uri? uri)
                    && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
                    return uri.Host;
            }
            return "source:" + _meta.Id;
        }

        /// <summary>
        /// Fetches an image with at most <see cref="MaxImageFetchesPerSource"/>
        /// per source inside the sidecar, queueing the rest HERE.
        ///
        /// Why: the continuous reader "loaded three chapters then stopped" until
        /// it was exited and re-entered. The sidecar runs each image fetch in a
        /// bare runBlocking, so nothing can cancel it once sent — a page the
        /// reader scrolled past keeps its OkHttp slot until the source answers,
        /// up to the 2-minute callTimeout. Readers abandon pages constantly (471
        /// cancelled stream requests in one session), so those dead fetches
        /// queued ahead of every new chapter's pages, and once the backlog grew
        /// past what drains in the reader's 30s budget, the next chapter's pages
        /// timed out. Leaving the reader "fixed" it only by letting it drain.
        ///
        /// Queueing on this side makes abandonment free: a request still
        /// waiting for a slot is withdrawn the moment its caller cancels, so the
        /// sidecar only ever holds work somebody still wants. It also keeps the
        /// sidecar's shared thread pool — which serves /health too — from filling
        /// with blocked image handlers.
        ///
        /// The slot is held until the SIDECAR finishes, not until the caller
        /// gives up: the fetch cannot be stopped once sent, so releasing early
        /// would just move the backlog back into the sidecar. It is bounded by
        /// the sidecar client's own timeout, so it can never be held forever.
        /// </summary>
        private async Task<ContentTypeStream> ThrottledImageAsync(Page page, CancellationToken token)
        {
            // Reader first, downloads never take the last slot — see SourceImageGate.
            SourceImageGate slots = ImageSlots.GetOrAdd(GateKey(page), _ => new SourceImageGate(MaxImageFetchesPerHost));
            await slots.AcquireAsync(ImageFetchPriority.IsInteractive, token).ConfigureAwait(false);   // abandoned while queued: gone at once
            if (token.IsCancellationRequested)
            {
                // Granted a slot in the same instant the caller gave up: hand it
                // straight back rather than spend it on a fetch nobody wants.
                slots.Release();
                token.ThrowIfCancellationRequested();
            }

            Task<ContentTypeStream> fetch;
            try
            {
                // Deliberately not the caller's token — see above.
                fetch = _client.ImageAsync(_meta.Id, page, CancellationToken.None);
            }
            catch
            {
                slots.Release();
                throw;
            }
            _ = fetch.ContinueWith(_ => slots.Release(), CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return await fetch.WaitAsync(token).ConfigureAwait(false);
        }

        public Task<ContentTypeStream> DownloadUrlAsync(string url, CancellationToken token = default)
        {
            // Route arbitrary URL fetches through the source's client via a synthetic single-page image.
            return ThrottledImageAsync(new Page { Index = 0, Url = url, ImageUrl = url }, token);
        }

        // Preferences: the sidecar's preference endpoints are being completed; until then these are
        // safe no-ops (sources still function; only source-specific settings are unavailable).
        public List<KeyPreference> GetPreferences() => new();
        public void SetPreference(int position, string value) { }
        public void SetPreference(KeyPreference preference) { }
        public void SetPreferences(IEnumerable<KeyPreference> preferences) { }
    }
}
