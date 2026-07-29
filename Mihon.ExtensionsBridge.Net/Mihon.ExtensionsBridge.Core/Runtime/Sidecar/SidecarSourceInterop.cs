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
        public Task<ContentTypeStream> GetPageImageAsync(Page page, CancellationToken token = default) => _client.ImageAsync(_meta.Id, page, token);

        public Task<ContentTypeStream> DownloadUrlAsync(string url, CancellationToken token = default)
        {
            // Route arbitrary URL fetches through the source's client via a synthetic single-page image.
            return _client.ImageAsync(_meta.Id, new Page { Index = 0, Url = url, ImageUrl = url }, token);
        }

        // Preferences: the sidecar's preference endpoints are being completed; until then these are
        // safe no-ops (sources still function; only source-specific settings are unavailable).
        public List<KeyPreference> GetPreferences() => new();
        public void SetPreference(int position, string value) { }
        public void SetPreference(KeyPreference preference) { }
        public void SetPreferences(IEnumerable<KeyPreference> preferences) { }
    }
}
