using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Mihon.ExtensionsBridge.Core.Runtime.Sidecar;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-15 audit: the JVM sidecar (127.0.0.1:9834, status :9835) answered
/// anyone on the host's loopback — an SSRF, or a page its own browser opened,
/// could run WebView scripts, read site-login cookies, rewrite /config or load
/// a jar. It now needs the per-launch secret the backend hands it, and the JSON
/// endpoints take only POST + application/json.
/// </summary>
public class SidecarSecretTests
{
    // ---- The backend side: a fresh secret, sent on every request ----

    [Fact]
    public void Each_launch_gets_a_new_32_byte_secret()
    {
        string a = SidecarAuthHandler.NewSecret(), b = SidecarAuthHandler.NewSecret();
        Assert.NotEqual(a, b);
        byte[] raw = Convert.FromBase64String(a.Replace('-', '+').Replace('_', '/') + "=");
        Assert.Equal(32, raw.Length);
        // Header- and environment-safe: no padding, no '+' or '/'.
        Assert.Matches("^[A-Za-z0-9_-]+$", a);
    }

    private sealed class Capture : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Seen = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") });
        }
    }

    private static string? Key(HttpRequestMessage r) =>
        r.Headers.TryGetValues(SidecarAuthHandler.HeaderName, out var v) ? Assert.Single(v) : null;

    [Fact]
    public async Task Every_request_carries_the_current_secret_including_the_status_port()
    {
        var capture = new Capture();
        var auth = new SidecarAuthHandler(capture) { Secret = "first" };
        using var http = new HttpClient(auth) { BaseAddress = new Uri("http://127.0.0.1:9834") };
        var client = new SidecarClient(http);

        await client.HealthAsync();
        await client.StatusAsync(9835);           // absolute URL on the other listener
        auth.Secret = "second";                    // a relaunch
        await client.HealthAsync();

        Assert.Equal(3, capture.Seen.Count);
        Assert.Equal("first", Key(capture.Seen[0]));
        Assert.Equal(9835, capture.Seen[1].RequestUri!.Port);
        Assert.Equal("first", Key(capture.Seen[1]));
        Assert.Equal("second", Key(capture.Seen[2]));
    }

    [Fact]
    public async Task Json_calls_are_posted_as_application_json()
    {
        var capture = new Capture();
        using var http = new HttpClient(new SidecarAuthHandler(capture) { Secret = "s" }) { BaseAddress = new Uri("http://127.0.0.1:9834") };
        await new SidecarClient(http).ConfigAsync(new Dictionary<string, string> { ["flareSolverrEnabled"] = "true" });

        HttpRequestMessage r = Assert.Single(capture.Seen);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal("application/json", r.Content!.Headers.ContentType!.MediaType);
        Assert.Equal("s", Key(r));
    }

    // ---- The JVM side: the real jar refuses everything without it ----
    //
    // Runs against the staged sidecar jar with a JDK 21+ (CI stages both before
    // `dotnet test`); returns early on a machine without them.

    private static string? FindJar()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string p = Path.Combine(d.FullName, "RenzoBackend", "sidecar", "AndroidCompat-1.0-all.jar");
            if (File.Exists(p)) return p;
        }
        return null;
    }

    private static string? FindJava()
    {
        foreach (string? home in new[] { Environment.GetEnvironmentVariable("JAVA_HOME"), "/opt/jdk21" })
        {
            if (string.IsNullOrEmpty(home)) continue;
            string java = Path.Combine(home, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(java)) return java;
        }
        return null;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static Process? StartSidecar(string java, string jar, int port, int statusPort, string? secret)
    {
        var psi = new ProcessStartInfo(java) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(jar);
        psi.ArgumentList.Add("extension.bridge.server.SidecarServer");
        psi.Environment["RENZO_SIDECAR_PORT"] = port.ToString();
        psi.Environment["RENZO_SIDECAR_STATUS_PORT"] = statusPort.ToString();
        psi.Environment["RENZO_SIDECAR_NO_JCEF"] = "1";
        psi.Environment.Remove(SidecarAuthHandler.EnvVar);
        if (secret != null) psi.Environment[SidecarAuthHandler.EnvVar] = secret;
        var p = Process.Start(psi);
        p?.BeginOutputReadLine();
        p?.BeginErrorReadLine();
        return p;
    }

    [Fact]
    public async Task The_sidecar_refuses_requests_without_the_secret()
    {
        string? jar = FindJar(), java = FindJava();
        if (jar == null || java == null) return;

        int port = FreePort(), statusPort = FreePort();
        string secret = SidecarAuthHandler.NewSecret();
        using Process proc = StartSidecar(java, jar, port, statusPort, secret)!;
        try
        {
            using var bare = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var auth = new SidecarAuthHandler { Secret = secret };
            using var http = new HttpClient(auth) { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
            var client = new SidecarClient(http);

            DateTime deadline = DateTime.UtcNow.AddSeconds(60);
            while (!await client.HealthAsync())
            {
                Assert.False(proc.HasExited, "sidecar exited during startup");
                Assert.True(DateTime.UtcNow < deadline, "sidecar never became healthy");
                await Task.Delay(250);
            }

            // The backend's own calls get through, on both listeners.
            Assert.NotNull(await client.StatusAsync(statusPort));

            string main = $"http://127.0.0.1:{port}";
            async Task<HttpStatusCode> Send(HttpMethod m, string url, string? key, string? contentType = "application/json")
            {
                using var req = new HttpRequestMessage(m, url);
                if (key != null) req.Headers.TryAddWithoutValidation(SidecarAuthHandler.HeaderName, key);
                if (contentType != null) req.Content = new StringContent("{}", Encoding.UTF8, contentType);
                using var resp = await bare.SendAsync(req);
                return resp.StatusCode;
            }

            // No secret, or the wrong one: refused everywhere, status listener included.
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(HttpMethod.Get, $"{main}/health", null, null));
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(HttpMethod.Get, $"http://127.0.0.1:{statusPort}/status", null, null));
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(HttpMethod.Post, $"{main}/config", null));
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(HttpMethod.Post, $"{main}/webview/script", "wrong"));
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(HttpMethod.Post, $"{main}/cookies/snapshot", secret + "x"));
            // A browser's CORS preflight never carries the header, so a page cannot get past it.
            Assert.Equal(HttpStatusCode.Unauthorized, await Send(HttpMethod.Options, $"{main}/sources/load", null, null));

            // With the secret, JSON endpoints still take only POST + application/json.
            Assert.Equal(HttpStatusCode.MethodNotAllowed, await Send(HttpMethod.Get, $"{main}/config", secret, null));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, await Send(HttpMethod.Post, $"{main}/config", secret, "text/plain"));
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, await Send(HttpMethod.Post, $"{main}/cookies/snapshot", secret, null));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, await Send(HttpMethod.Post, $"{main}/health", secret));

            // And a well-formed call works.
            Assert.Empty(await client.SnapshotCookiesAsync("example.com"));
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }

    [Fact]
    public async Task The_sidecar_will_not_start_without_a_secret()
    {
        string? jar = FindJar(), java = FindJava();
        if (jar == null || java == null) return;

        using Process proc = StartSidecar(java, jar, FreePort(), FreePort(), secret: null)!;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await proc.WaitForExitAsync(cts.Token);
            Assert.Equal(2, proc.ExitCode);
        }
        finally
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        }
    }
}
