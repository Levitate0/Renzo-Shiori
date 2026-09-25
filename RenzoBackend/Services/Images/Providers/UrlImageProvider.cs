using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Bridge;
using Microsoft.Extensions.Options;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using System.Net;
using System.Security.Cryptography;

namespace RenzoBackend.Services.Images.Providers
{
    public class UrlImageProvider : IImageProvider
    {
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _factory;
        private readonly AppDbContext _db;
        private readonly CacheOptions _options;
        private readonly MihonBridgeService _mihonBridgeService;
        private readonly CloudflareClearanceService _clearance;


        public UrlImageProvider(ILogger<UrlImageProvider> logger, IHttpClientFactory factory, AppDbContext db, IOptions<CacheOptions> options, MihonBridgeService mihonBridgeService, CloudflareClearanceService clearance)
        {
            _logger = logger;
            _factory = factory;
            _db = db;
            _options = options.Value;
            _mihonBridgeService = mihonBridgeService;
            _clearance = clearance;
        }

        public static async Task<string> ComputeMd5HashFromStreamAsync(Stream stream, CancellationToken token = default)
        {
            using (var md5 = MD5.Create())
            {
                byte[] hash = await md5.ComputeHashAsync(stream, token).ConfigureAwait(false);
                return Convert.ToBase64String(hash);
            }
        }

        TimeSpan ResolveCacheDuration(HttpResponseMessage responseMessage)
        {
            var cacheControl = responseMessage.Headers.CacheControl;
            var maxAge = cacheControl?.MaxAge ?? cacheControl?.SharedMaxAge ?? cacheControl?.MaxStaleLimit;

            if (maxAge.HasValue && maxAge.Value > TimeSpan.Zero)
            {
                return maxAge.Value;
            }

            var fallbackDays = _options.AgeInDays > 0 ? _options.AgeInDays : 1;
            return TimeSpan.FromDays(fallbackDays);
        }

        static string NormalizeExtension(string? extension)
        {
            if (string.IsNullOrWhiteSpace(extension))
            {
                return string.Empty;
            }

            var trimmed = extension.Trim();
            return trimmed.StartsWith('.') ? trimmed : "." + trimmed.TrimStart('.');
        }
        public bool CanProcess(string url)
        {
            if (string.IsNullOrEmpty(url))
                return false;
            if (url.StartsWith("http://") || url.StartsWith("https://"))
                return true;
            return false;
        }
        /// <summary>
        /// How long to leave a cover alone after a failed fetch.
        ///
        /// Without this, a cover that cannot be fetched is retried on EVERY
        /// request, and the retry is not cheap: a non-OK response falls back to
        /// the extension, which goes through the sidecar and can trigger a
        /// Cloudflare browser solve, up to three times per image. A Browse grid
        /// is ~40 covers, so one screenful of a Cloudflare-protected source
        /// (kagane.to answers 403 to a plain fetch, and to any User-Agent or
        /// Referer) could queue on the order of a hundred browser solves — and do
        /// it again on the next scroll, competing with the sidecar work that
        /// chapters and Browse itself need. That is the "images take forever"
        /// report.
        ///
        /// Only failures are affected: an image already in the file cache is
        /// served without ever reaching this path.
        /// </summary>
        private static readonly TimeSpan FailureRetryCooldown = TimeSpan.FromMinutes(30);

        /// <summary>Back off after a failed fetch so the next request is cheap.</summary>
        private async Task MarkFailedAsync(EtagCacheEntity cache)
        {
            try
            {
                cache.NextUpdateUTC = DateTime.UtcNow.Add(FailureRetryCooldown);
                await _db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Losing the back-off costs speed, never correctness.
                _logger.LogWarning(ex, "Could not record the fetch failure for {Key}", cache.Key);
            }
        }

        public async Task<Stream?> ObtainStreamAsync(EtagCacheEntity cache, CancellationToken token)
        {
            string directory = Path.Combine(_options.CachePath, cache.Key.Substring(0, 2));
            if (!string.IsNullOrEmpty(cache.Extension))
            {
                string baseFile = Path.Combine(directory, cache.Key.Substring(2)) + cache.Extension;
                if (File.Exists(baseFile))
                {
                    return File.OpenRead(baseFile);
                }
            }
            // Nothing cached, and a recent attempt already failed — answer now
            // instead of paying for the same doomed fetch again.
            if (cache.NextUpdateUTC > DateTime.UtcNow)
                return null;
            var httpClient = _factory.CreateClient(nameof(ThumbCacheService));
            await UpdateCacheWithRemoteAsync(cache, httpClient, token).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(cache.Extension))
            {
                string baseFile = Path.Combine(directory, cache.Key.Substring(2)) + cache.Extension;
                if (File.Exists(baseFile))
                {
                    return File.OpenRead(baseFile);
                }
            }
            return null;
        }

        /// <summary>
        /// Repeats the request with Cloudflare clearance for this host. Returns the
        /// successful response, or null to let the caller fall through.
        /// </summary>
        private async Task<HttpResponseMessage?> TryWithClearanceAsync(EtagCacheEntity cache, HttpClient httpClient, CancellationToken token)
        {
            if (!Uri.TryCreate(cache.Url, UriKind.Absolute, out Uri? uri))
                return null;

            CloudflareClearance? clearance = await _clearance.GetAsync(uri, token).ConfigureAwait(false);
            if (clearance == null)
                return null;

            HttpResponseMessage? attempt = await SendWithClearanceAsync(uri, httpClient, clearance, token).ConfigureAwait(false);
            if (attempt == null)
                return null;
            if (attempt.IsSuccessStatusCode)
                return attempt;

            // Clearance can expire early, or be bound to an address that has since
            // changed. Re-solve once before giving up.
            attempt.Dispose();
            _clearance.Invalidate(uri);
            clearance = await _clearance.GetAsync(uri, token).ConfigureAwait(false);
            if (clearance == null)
                return null;

            attempt = await SendWithClearanceAsync(uri, httpClient, clearance, token).ConfigureAwait(false);
            if (attempt == null)
                return null;
            if (attempt.IsSuccessStatusCode)
                return attempt;
            attempt.Dispose();
            return null;
        }

        private static async Task<HttpResponseMessage?> SendWithClearanceAsync(Uri uri, HttpClient httpClient, CloudflareClearance clearance, CancellationToken token)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                // The cookie is only honoured for the User-Agent it was issued to.
                request.Headers.TryAddWithoutValidation("User-Agent", clearance.UserAgent);
                request.Headers.TryAddWithoutValidation("Cookie", "cf_clearance=" + clearance.Cookie);
                request.Headers.TryAddWithoutValidation("Referer", uri.GetLeftPart(UriPartial.Authority) + "/");
                return await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }

        public async Task UpdateCacheWithRemoteAsync(EtagCacheEntity cache, HttpClient httpClient, CancellationToken token = default)
        {
            if (string.IsNullOrEmpty(cache.Url))
            {
                _logger.LogWarning($"Cache URL is null or empty for {cache.Key}");
                return;
            }
            try
            {
                string directory = Path.Combine(_options.CachePath, cache.Key.Substring(0, 2));
                if (!Directory.Exists(directory))
                    Directory.CreateDirectory(directory);
                string baseFile = Path.Combine(directory, cache.Key.Substring(2));
                string originalFile = null;
                if (!string.IsNullOrEmpty(cache.Extension))
                {
                    originalFile = baseFile + cache.Extension;
                    if (!File.Exists(originalFile))
                        cache.ExternalEtag = string.Empty;
                }
                using var memoryStream = new MemoryStream();
                string mediaType = "application/octet-stream";
                using var request = new HttpRequestMessage(HttpMethod.Get, cache.Url);
                if (!string.IsNullOrWhiteSpace(cache.ExternalEtag))
                {
                    request.Headers.TryAddWithoutValidation("If-None-Match", cache.ExternalEtag);
                }
                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

                if (response.Headers.ETag != null)
                {
                    cache.ExternalEtag = response.Headers.ETag.Tag;
                }

                TimeSpan cacheDuration = ResolveCacheDuration(response);
                // Reassigned below if a clearance-carrying retry supersedes this response.

                if (response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.NotModified)
                {
                    cache.NextUpdateUTC = DateTime.UtcNow.Add(cacheDuration);
                    await _db.SaveChangesAsync().ConfigureAwait(false);
                    return;
                }
                HttpResponseMessage? cleared = null;
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.ServiceUnavailable)
                {
                    // Cloudflare. Solve once for this host, then repeat the ORDINARY
                    // request carrying cf_clearance — handing the url to the
                    // extension instead returns the rendered challenge page, which
                    // is why these covers used to come back as a few hundred bytes
                    // of HTML and never render.
                    cleared = await TryWithClearanceAsync(cache, httpClient, token).ConfigureAwait(false);
                }

                if (cleared != null)
                {
                    response.Dispose();
                    await cleared.Content.CopyToAsync(memoryStream, token).ConfigureAwait(false);
                    string? clearedMedia = cleared.Content.Headers.ContentType?.MediaType;
                    if (!string.IsNullOrEmpty(clearedMedia))
                        mediaType = clearedMedia;
                    cacheDuration = ResolveCacheDuration(cleared);
                    cleared.Dispose();
                }
                else if (response.StatusCode != HttpStatusCode.OK)
                {
                    //try source
                    if (cache.MihonProviderId != null)
                    {

                        ISourceInterop interop = await _mihonBridgeService.SourceFromProviderIdAsync(cache.MihonProviderId).ConfigureAwait(false);
                        if (interop != null)
                        {
                            ContentTypeStream? image;
                            int retries = 2; //try twice with the interop in case of referer or missing headers.
                            do
                            {
                                string message = "Error downloading the image for {Key}";
                                if (retries == 2)
                                    message = "Error downloading the image for {Key}. Retrying...";
                                image = await _mihonBridgeService.MihonErrorWrapperAsync(
                                    () => interop.DownloadUrlAsync(cache.Url, token),
                                    message, cache.Key).ConfigureAwait(false);
                                if (image != null)
                                    break;
                            } while (retries-- > 0);
                            if (image == null)
                            {
                                await MarkFailedAsync(cache).ConfigureAwait(false);
                                return; //Warning already logged in the wrapper
                            }
                            await image.CopyToAsync(memoryStream, token).ConfigureAwait(false);
                        }
                        else
                        {
                            _logger.LogWarning("Error downloading the image for {Key}. Http error: {StatusCode}", cache.Key, response.StatusCode);
                            await MarkFailedAsync(cache).ConfigureAwait(false);
                            return;
                        }
                    }
                    else
                    {
                        _logger.LogWarning("Error downloading the image for {Key}. Http error: {StatusCode}", cache.Key, response.StatusCode);
                        await MarkFailedAsync(cache).ConfigureAwait(false);
                        return;
                    }
                }
                else
                    await response.Content.CopyToAsync(memoryStream, token).ConfigureAwait(false);
                string? med = response.Content.Headers.ContentType?.MediaType;
                if (!string.IsNullOrEmpty(med))
                    mediaType = med;
                if (memoryStream.Length == 0)
                {
                    _logger.LogWarning("Received empty payload when refreshing cache for {Key}", cache.Key);
                    await MarkFailedAsync(cache).ConfigureAwait(false);
                    return;
                }
                memoryStream.Position = 0;
                cache.Etag = await ComputeMd5HashFromStreamAsync(memoryStream, token).ConfigureAwait(false);
                memoryStream.Position = 0;
                (string? detectedContentType, string? detectedExtension) = memoryStream.GetImageMimeTypeAndExtension();
                var contentType = !string.IsNullOrWhiteSpace(detectedContentType)
                    ? detectedContentType
                    : mediaType;

                var normalizedExtension = NormalizeExtension(detectedExtension);
                if (string.IsNullOrEmpty(normalizedExtension))
                {
                    normalizedExtension = NormalizeExtension(Path.GetExtension(cache.Url));
                    if (string.IsNullOrEmpty(normalizedExtension))
                    {
                        normalizedExtension = ".bin";
                    }
                }
                var targetFile = baseFile + normalizedExtension;
                if (originalFile != null && File.Exists(originalFile) && (originalFile != baseFile))
                {
                    try
                    {
                        File.Delete(originalFile);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete old cache file for {Key}", cache.Key);
                    }
                }

                memoryStream.Position = 0;
                using (var fileStream = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, useAsync: true))
                {
                    await memoryStream.CopyToAsync(fileStream, token).ConfigureAwait(false);
                }
                cache.Extension = normalizedExtension;
                cache.ContentType = contentType;
                cache.NextUpdateUTC = DateTime.UtcNow.Add(cacheDuration);
                await _db.SaveChangesAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw; // the caller gave up; not a fetch failure worth backing off
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating cache for key {Key}", cache.Key);
                await MarkFailedAsync(cache).ConfigureAwait(false);
            }
        }
    }
}
