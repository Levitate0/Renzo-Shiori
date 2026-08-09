using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using Mihon.ExtensionsBridge.Core.Models;
using Mihon.ExtensionsBridge.Core.Abstractions;


namespace Mihon.ExtensionsBridge.Core.Services
{
    /// <summary>
    /// Provides functionality to download and populate Tachiyomi extensions from a remote repository endpoint.
    /// </summary>
    /// <remarks>
    /// This service fetches a JSON payload from the specified repository URL, deserializes it into a collection of
    /// <see cref="TachiyomiExtension"/> instances, and persists the result using the configured working folder structure.
    /// </remarks>
    public class RepositoryDownloader : IRepositoryDownloader
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IWorkingFolderStructure _folder;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="RepositoryDownloader"/> class.
        /// </summary>
        /// <param name="httpClientFactory">The HTTP client factory used to create configured <see cref="HttpClient"/> instances.</param>
        /// <param name="folder">The working folder structure used to persist repository data.</param>
        /// <param name="logger">The logger used to write diagnostic and operational logs.</param>
        /// <exception cref="ArgumentNullException">Thrown when any of the provided arguments are <c>null</c>.</exception>
        public RepositoryDownloader(IHttpClientFactory httpClientFactory, IWorkingFolderStructure folder, ILogger<RepositoryDownloader> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _folder = folder ?? throw new ArgumentNullException(nameof(folder));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Creates and configures an HTTP client for downloading repository data.
        /// </summary>
        /// <returns>A configured <see cref="HttpClient"/> instance with a custom user agent and timeout.</returns>
        /// <remarks>
        /// The client is created via <see cref="IHttpClientFactory"/> to leverage central configuration and lifetime management.
        /// A product-specific user agent is attached and a five-minute timeout is set to accommodate large payloads.
        /// </remarks>
        private HttpClient CreateHttpClient()
        {
            var client = _httpClientFactory.CreateClient(nameof(RepositoryDownloader));
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ExtensionBridge", "1.0"));
            client.Timeout = TimeSpan.FromMinutes(5);
            return client;
        }


        // index.json (the v2 index in JSON form) is tried FIRST and index.min.json
        // last, which is the reverse of the obvious order. Keiyoushi turned
        // index.min.json into a two-entry "your client is outdated" tombstone, so a
        // downloader that prefers it fetches 200 OK, parses cleanly, and installs a
        // catalogue of two — a silent downgrade from ~1370 extensions. Preferring the
        // fuller index means v2 repos work and genuine v1 repos (which have no
        // index.json quirk) still resolve.
        private static string[] index = ["index.json", "index.min.json"];

        private static string[] repos = ["repo.json"];

        /// <summary>
        /// Index filenames a user may paste as the repository URL. The URL is kept
        /// exactly as entered — see <see cref="MiscExtensions.RepoFromUrl"/> — so
        /// sub-paths are composed from the stripped base while the stored URL stays
        /// whatever the user typed.
        /// </summary>
        private static readonly string[] IndexFileNames = ["index.pb", "index.min.json", "index.json"];

        private static bool PointsAtIndexFile(string url) =>
            IndexFileNames.Any(f => url.TrimEnd('/').EndsWith(f, StringComparison.InvariantCultureIgnoreCase));

        /// <summary>
        /// Downloads, deserializes, and persists Tachiyomi extensions for the provided repository.
        /// </summary>
        /// <param name="repository">The repository descriptor containing the source URL and metadata to populate.</param>
        /// <param name="cancellationToken">A token to observe while awaiting the operation.</param>
        /// <returns>The updated <see cref="TachiyomiRepository"/> instance containing extensions and last update timestamp.</returns>
        /// <exception cref="ArgumentNullException">Thrown when <paramref name="repository"/> is <c>null</c>.</exception>
        /// <exception cref="ArgumentException">Thrown when the repository URL is <c>null</c>, empty, or whitespace.</exception>
        /// <exception cref="OperationCanceledException">Thrown if the operation is canceled via <paramref name="cancellationToken"/>.</exception>
        /// <exception cref="HttpRequestException">Thrown when the HTTP request is unsuccessful.</exception>
        /// <exception cref="JsonException">Thrown when the JSON payload cannot be deserialized.</exception>
        /// <remarks>
        /// This method:
        /// - Validates input arguments.
        /// - Performs an HTTP GET to the repository URL.
        /// - Streams and deserializes the JSON payload into <see cref="List{T}"/> of <see cref="TachiyomiExtension"/>.
        /// - Updates the repository with the extensions and the current UTC timestamp.
        /// - Persists the repository using <see cref="IWorkingFolderStructure.SaveExtensionAsync(TachiyomiRepository, CancellationToken)"/>
        /// </remarks>
        public async Task<TachiyomiRepository> PopulateExtensionsAsync(TachiyomiRepository repository, CancellationToken cancellationToken = default)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (string.IsNullOrWhiteSpace(repository.Url)) throw new ArgumentException("Repository URL cannot be null or whitespace.", nameof(repository));
            _logger.LogInformation("Starting repository download from URL: {RepositoryUrl}", repository.Url);

            HttpResponseMessage? response = null;
            string? usedUrl = null;

            try
            {
                var client = CreateHttpClient();

                // The stored URL is left exactly as the user entered it (it may point
                // straight at an index file); everything below it is composed from the
                // stripped base instead, so "…/repo/index.pb" does not turn into
                // "…/repo/index.pb/repo.json".
                var baseUrl = MiscExtensions.RepoFromUrl(repository.Url);

                foreach (var fileName in repos)
                {
                    var candidateUrl = baseUrl.CombineUrl(fileName);
                    using var request = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                    var tempResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                    if (tempResponse.StatusCode != System.Net.HttpStatusCode.OK)
                    {
                        tempResponse.Dispose();
                        _logger.LogDebug("Index not found at {CandidateUrl}. Trying next candidate...", candidateUrl);
                        continue;
                    }
                    response = tempResponse;
                    usedUrl = candidateUrl;
                    break;
                }
                if (response!=null)
                {
                    await using var rstream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    var reposMeta = await JsonSerializer.DeserializeAsync<RepositoryMeta>(rstream, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    }, cancellationToken).ConfigureAwait(false);
                    if (reposMeta != null)
                    {
                        repository.WebSite = reposMeta.meta.website;
                        repository.Name = reposMeta.meta.name;
                        repository.Fingerprint = reposMeta.meta.signingKeyFingerprint;
                    }
                    response.Dispose();
                    response = null;
                }

                // Honour a URL that points straight at an index file, then fall back to
                // the usual candidates. index.pb is the canonical v2 index but it is
                // protobuf; the same data is published as index.json, so that is what
                // gets fetched — the URL the user entered is still what we store.
                var candidates = new List<string>();
                if (PointsAtIndexFile(repository.Url))
                {
                    if (repository.Url.TrimEnd('/').EndsWith("index.pb", StringComparison.InvariantCultureIgnoreCase))
                    {
                        _logger.LogInformation(
                            "{RepositoryUrl} is a protobuf index; reading the JSON twin at {JsonUrl} (same data, no .proto needed).",
                            repository.Url, baseUrl.CombineUrl("index.json"));
                    }
                    else
                    {
                        candidates.Add(repository.Url);
                    }
                }
                candidates.AddRange(index.Select(f => baseUrl.CombineUrl(f)));

                foreach (var candidateUrl in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, candidateUrl);
                    var tempResponse = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                    if (tempResponse.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        tempResponse.Dispose();
                        _logger.LogDebug("Index not found at {CandidateUrl}. Trying next candidate...", candidateUrl);
                        continue;
                    }

                    tempResponse.EnsureSuccessStatusCode();
                    response = tempResponse;
                    usedUrl = candidateUrl;
                    break;
                }
                // Fallback to original URL if no index candidate succeeded
                if (response == null)
                {
                    throw new HttpRequestException("No valid index file found in the repository.");
                }

                _logger.LogInformation("Resolved repository index at: {ResolvedUrl}", usedUrl);

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

                // v1 is a bare array of extensions; v2 is an object wrapping
                // extensionList.extensions. Both are still served, so branch on the
                // shape rather than on the filename we happened to resolve.
                List<TachiyomiExtension> extensions;
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    extensions = document.RootElement.Deserialize<List<TachiyomiExtension>>(jsonOptions)
                        ?? new List<TachiyomiExtension>();
                    WarnIfTombstoneIndex(extensions, usedUrl);
                }
                else
                {
                    var v2 = document.RootElement.Deserialize<RepositoryIndexV2>(jsonOptions);
                    extensions = MapV2Extensions(v2);
                    if (!string.IsNullOrWhiteSpace(v2?.Name))
                        repository.Name = v2!.Name!;
                    if (!string.IsNullOrWhiteSpace(v2?.SigningKey))
                        repository.Fingerprint = v2!.SigningKey!;
                    if (!string.IsNullOrWhiteSpace(v2?.Contact?.Website))
                        repository.WebSite = v2!.Contact!.Website!;
                }

                repository.Extensions = extensions;
                repository.LastUpdatedUTC = DateTimeOffset.UtcNow;

                _logger.LogInformation("Downloaded {ExtensionCount} extensions from {ResolvedUrl}. Saving to working folder...", repository.Extensions.Count, usedUrl);
                await _folder.SaveOnlineRepositoryAsync(repository, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Completed extension download and save for {ResolvedUrl}.", usedUrl);

                return repository;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Extension download canceled for {RepositoryUrl}.", repository.Url);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading extensions from {RepositoryUrl}.", repository.Url);
                throw;
            }
            finally
            {
                response?.Dispose();
            }
        }

        /// <summary>
        /// Flattens a v2 index into the v1-shaped <see cref="TachiyomiExtension"/> the
        /// rest of the bridge already understands, so nothing downstream has to know
        /// which index version a repo served.
        /// </summary>
        private static List<TachiyomiExtension> MapV2Extensions(RepositoryIndexV2? index)
        {
            var source = index?.ExtensionList?.Extensions;
            if (source == null || source.Count == 0)
                return new List<TachiyomiExtension>();

            var mapped = new List<TachiyomiExtension>(source.Count);
            foreach (var entry in source)
            {
                if (string.IsNullOrWhiteSpace(entry.PackageName))
                    continue;

                string apkUrl = entry.Resources?.ApkUrl ?? string.Empty;

                // v1 carried a bare filename and composed the URL; v2 carries the URL.
                // Keep the filename too — it is what the APK is saved as on disk.
                string apkFile = string.Empty;
                if (!string.IsNullOrWhiteSpace(apkUrl))
                {
                    var lastSegment = apkUrl.Split('?')[0].TrimEnd('/');
                    int slash = lastSegment.LastIndexOf('/');
                    apkFile = slash >= 0 ? lastSegment[(slash + 1)..] : lastSegment;
                }

                // v1 put a single language on the extension; v2 only has per-source
                // languages. Collapse to the shared one, or "all" when they differ —
                // which is the same convention the v1 indexes used.
                var languages = entry.Sources
                    .Select(s => s.Language)
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                string language = languages.Count == 1 ? languages[0]! : "all";

                // "extensionLib" is major.minor and versionCode the patch, which is how
                // versionName is built; parse the patch for the numeric VersionCode the
                // update check compares on, and fall back to the trailing component of
                // versionName if the field is missing.
                if (!int.TryParse(entry.VersionCode, out int versionCode))
                {
                    var tail = entry.VersionName?.Split('.').LastOrDefault();
                    int.TryParse(tail, out versionCode);
                }

                mapped.Add(new TachiyomiExtension
                {
                    Name = entry.Name ?? entry.PackageName!,
                    Package = entry.PackageName!,
                    Apk = apkFile,
                    ApkUrl = string.IsNullOrWhiteSpace(apkUrl) ? null : apkUrl,
                    IconUrl = string.IsNullOrWhiteSpace(entry.Resources?.IconUrl) ? null : entry.Resources!.IconUrl,
                    Language = language,
                    VersionCode = versionCode,
                    Version = entry.VersionName ?? string.Empty,
                    Nsfw = string.Equals(entry.ContentWarning, "CONTENT_WARNING_NSFW", StringComparison.OrdinalIgnoreCase) ? 1 : 0,
                    Sources = entry.Sources.Select(s => new TachiyomiSource
                    {
                        Id = s.Id ?? string.Empty,
                        Name = s.Name ?? entry.Name ?? string.Empty,
                        Language = s.Language ?? language,
                        BaseUrl = s.HomeUrl ?? string.Empty,
                    }).ToList(),
                });
            }

            return mapped;
        }

        /// <summary>
        /// A v1 index that contains nothing but the upgrade-nag placeholders is a
        /// deprecation notice, not a catalogue. Installing it would replace every
        /// extension with two stubs, so say so loudly rather than reporting success.
        /// </summary>
        private void WarnIfTombstoneIndex(List<TachiyomiExtension> extensions, string? usedUrl)
        {
            if (extensions.Count == 0 || extensions.Count > 2)
                return;

            bool allPlaceholders = extensions.All(e =>
                e.Package?.EndsWith(".keiyoushi", StringComparison.OrdinalIgnoreCase) == true ||
                e.Package?.EndsWith(".mihon", StringComparison.OrdinalIgnoreCase) == true);

            if (allPlaceholders)
            {
                _logger.LogWarning(
                    "{ResolvedUrl} returned only upgrade-notice placeholders ({Count}). This repo has moved to the v2 index; " +
                    "point it at the repo root or index.pb so index.json is used instead.",
                    usedUrl, extensions.Count);
            }
        }

        /// <summary>
        /// Downloads the APK and icon for a specific extension into the working folder structure.
        /// </summary>
        /// <param name="repository">The repository that hosts the extension artifacts.</param>
        /// <param name="extension">The extension metadata used to resolve artifact names and version.</param>
        /// <param name="force">If true, forces re-download even if files already exist.</param>
        /// <param name="cancellationToken">A token to cancel the operation.</param>
        public async Task DownloadExtensionAsync(TachiyomiRepository repository, ExtensionWorkUnit workUnit, CancellationToken cancellationToken = default)
        {
            if (repository == null) throw new ArgumentNullException(nameof(repository));
            if (workUnit == null) throw new ArgumentNullException(nameof(workUnit));
            if (workUnit.Entry==null) throw new ArgumentException("Extension entry cannot be null.", nameof(workUnit));
            if (workUnit.Entry.Extension == null) throw new ArgumentException("Extension metadata cannot be null.", nameof(workUnit));
            if (string.IsNullOrWhiteSpace(repository.Url)) throw new ArgumentException("Repository URL cannot be null or whitespace.", nameof(repository));
            if (string.IsNullOrWhiteSpace(workUnit.Entry.Extension.Apk)) throw new ArgumentException("Extension APK cannot be null or whitespace.", nameof(workUnit));
            if (string.IsNullOrWhiteSpace(workUnit.Entry.Extension.Version)) throw new ArgumentException("Extension Version cannot be null or whitespace.", nameof(workUnit));
            if (string.IsNullOrWhiteSpace(workUnit.Entry.Extension.Package)) throw new ArgumentException("Extension Package cannot be null or whitespace.", nameof(workUnit));

            // GetApkUrl uses the absolute URL a v2 index supplies (often a CDN host)
            // and otherwise composes "{base}/apk/{file}" from the STRIPPED base — the
            // stored URL may point straight at an index file, and concatenating onto
            // that produced "…/index.pb/apk/….apk".
            var apkUrl = workUnit.Entry.Extension.GetApkUrl(repository);

            var apkDestination = Path.Combine(workUnit.WorkingFolder.Path, workUnit.Entry.Extension.Apk);
            var client = CreateHttpClient();
            try
            {
                await DownloadWithLoggingAsync(client, label: "APK", apkUrl, apkDestination, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Download canceled for {Package} v{Version}.", workUnit.Entry.Extension.Package, workUnit.Entry.Extension.Version);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error downloading artifacts for {Package} v{Version}.", workUnit.Entry.Extension.Package, workUnit.Entry.Extension.Version);
                throw;
            }
            workUnit.Entry.Name = workUnit.Entry.Extension.GetName();
            workUnit.Entry.DownloadUTC = DateTimeOffset.UtcNow;
            workUnit.Entry.DownloadUrl = apkUrl;
            workUnit.Entry.Apk = await apkDestination.CalculateFileHashAsync(cancellationToken).ConfigureAwait(false);
          
        }

        private async Task DownloadWithLoggingAsync(HttpClient httpClient, string label, string url, string destination, CancellationToken ct)
        {
            _logger.LogInformation("Downloading {Label} from {Url} -> {Destination}", label, url, destination);
            try
            {
                await DownloadFileAsync(httpClient, url, destination, ct).ConfigureAwait(false);
                _logger.LogInformation("Downloaded {Label} to {Destination}", label, destination);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download {Label} from {Url}", label, url);
                throw;
            }
        }



        private static async Task DownloadFileAsync(HttpClient client, string url, string destinationPath, CancellationToken cancellationToken)
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            await using var network = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using var file = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await network.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }
}