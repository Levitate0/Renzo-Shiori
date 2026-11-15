using System.Security.Cryptography;

namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>
    /// Puts the sidecar's per-launch secret on every request to it.
    ///
    /// The JVM listens on 127.0.0.1, but with host networking that is the HOST's
    /// loopback, and anything that can make a request there — an SSRF in some
    /// other endpoint, or a page the sidecar's own browser opens — could otherwise
    /// run scripts in the WebView, read or plant site-login cookies, rewrite the
    /// proxy settings or load a jar. A fresh secret per launch, handed to the JVM
    /// in its environment (never on the command line, which any local user can
    /// read in /proc/*/cmdline), means only the process that spawned it can talk
    /// to it. The header is custom on purpose: a browser cannot send it
    /// cross-origin without a CORS preflight, which the sidecar refuses.
    /// </summary>
    public sealed class SidecarAuthHandler : DelegatingHandler
    {
        /// <summary>Header the sidecar checks (SidecarServer.kt reads the same name).</summary>
        public const string HeaderName = "X-Renzo-Sidecar-Key";

        /// <summary>Environment variable the JVM reads the secret from.</summary>
        public const string EnvVar = "RENZO_SIDECAR_SECRET";

        private volatile string? _secret;

        public SidecarAuthHandler() : base(new HttpClientHandler()) { }

        public SidecarAuthHandler(HttpMessageHandler inner) : base(inner) { }

        /// <summary>
        /// The secret of the JVM currently running. Replaced on every launch, so a
        /// stale process left on the port (or anything else squatting it) answers
        /// 401 and fails the health wait instead of being mistaken for ours.
        /// </summary>
        public string? Secret
        {
            get => _secret;
            set => _secret = value;
        }

        /// <summary>32 bytes from the OS CSPRNG, URL-safe base64 (header- and env-safe).</summary>
        public static string NewSecret() =>
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? secret = _secret;
            if (!string.IsNullOrEmpty(secret))
            {
                request.Headers.Remove(HeaderName);
                request.Headers.TryAddWithoutValidation(HeaderName, secret);
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}
