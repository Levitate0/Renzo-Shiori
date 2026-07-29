using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Models.Abstractions;

namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    public sealed class SidecarOptions
    {
        /// <summary>Path to the java executable (bundled JRE in the container).</summary>
        public string JavaPath { get; set; } = Environment.GetEnvironmentVariable("RENZO_SIDECAR_JAVA") ?? "java";
        /// <summary>Path to the AndroidCompat fat jar that contains the sidecar server.</summary>
        public string JarPath { get; set; } = Environment.GetEnvironmentVariable("RENZO_SIDECAR_JAR") ?? "sidecar/AndroidCompat-1.0-all.jar";
        public int Port { get; set; } = int.TryParse(Environment.GetEnvironmentVariable("RENZO_SIDECAR_PORT"), out var p) ? p : 9834;
        public string MaxHeap { get; set; } = Environment.GetEnvironmentVariable("RENZO_SIDECAR_XMX") ?? "1500m";
        public string DataRoot { get; set; } = "";
        public string TempRoot { get; set; } = "";
        /// <summary>Directory holding the bundled enjarify python package (for /convert).</summary>
        public string? EnjarifyDir { get; set; } = Environment.GetEnvironmentVariable("RENZO_ENJARIFY_DIR");
        public bool DisableJcef { get; set; } = Environment.GetEnvironmentVariable("RENZO_SIDECAR_NO_JCEF") == "1";
    }

    /// <summary>
    /// Owns the JVM sidecar process: launches it (<c>java -Xverify:none -cp fat.jar
    /// extension.bridge.server.SidecarServer</c>), waits for health, calls /setup, and exposes a
    /// <see cref="SidecarClient"/>. Restarts the process if it dies. One instance per app.
    /// </summary>
    public sealed class SidecarProcessManager : IAsyncDisposable
    {
        private readonly SidecarOptions _opts;
        private readonly IWorkingFolderStructure _folder;
        private readonly ILogger _logger;
        private readonly HttpClient _http;
        private readonly SemaphoreSlim _startLock = new(1, 1);
        private Process? _proc;
        private volatile bool _ready;

        public SidecarClient Client { get; }

        public SidecarProcessManager(SidecarOptions opts, IWorkingFolderStructure folder, ILogger logger)
        {
            _opts = opts;
            _folder = folder;
            _logger = logger;
            _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{opts.Port}"), Timeout = TimeSpan.FromMinutes(3) };
            Client = new SidecarClient(_http);
        }

        public async Task EnsureStartedAsync(CancellationToken token = default)
        {
            if (_ready && _proc is { HasExited: false }) return;
            await _startLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (_ready && _proc is { HasExited: false }) return;
                // roots are only valid after the bridge/folder is initialized, so read them here.
                // Use a dedicated subdir so the sidecar's JCEF/cookies/config don't collide with the
                // in-process IKVM runtime while both coexist during migration.
                var dataRoot = string.IsNullOrEmpty(_opts.DataRoot) ? Path.Combine(_folder.AndroidFolder, "sidecar") : _opts.DataRoot;
                var tempRoot = string.IsNullOrEmpty(_opts.TempRoot) ? _folder.TempFolder : _opts.TempRoot;
                Directory.CreateDirectory(dataRoot);
                await StartProcessAsync(token).ConfigureAwait(false);
                await WaitHealthyAsync(TimeSpan.FromSeconds(60), token).ConfigureAwait(false);
                await Client.SetupAsync(dataRoot, tempRoot, token).ConfigureAwait(false);
                _ready = true;
                _logger.LogInformation("Sidecar ready on 127.0.0.1:{Port}.", _opts.Port);
            }
            finally { _startLock.Release(); }
        }

        private Task StartProcessAsync(CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _opts.JavaPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            // STILL LOAD-BEARING — do not remove without fixing the converter first.
            // Deprecated since JDK 13 and slated for removal, so this is on borrowed
            // time, but the enjarify path still emits bytecode the verifier rejects:
            // dropping the flag broke tachiyomi-en.allanime v1.6.25 with
            //   (method: getFilterList) Call to wrong initialization method
            // Note a full -Xverify:all sweep of the 53 already-converted jars on disk
            // (3,248 classes) passed cleanly — the bad output only appears in NEWLY
            // converted extensions, so verifying the existing cache proves nothing.
            // The real fix is in SidecarConvert's enjarify/dex2jar merge.
            psi.ArgumentList.Add("-Xverify:none");
            psi.ArgumentList.Add($"-Xmx{_opts.MaxHeap}");
            psi.ArgumentList.Add("-cp");
            psi.ArgumentList.Add(_opts.JarPath);
            psi.ArgumentList.Add("extension.bridge.server.SidecarServer");
            psi.Environment["RENZO_SIDECAR_PORT"] = _opts.Port.ToString();
            if (_opts.DisableJcef) psi.Environment["RENZO_SIDECAR_NO_JCEF"] = "1";
            if (!string.IsNullOrEmpty(_opts.EnjarifyDir)) psi.Environment["RENZO_ENJARIFY_DIR"] = _opts.EnjarifyDir!;
            // The app runs with LD_LIBRARY_PATH pointed at IKVM's native libs (/app/ikvm/.../bin).
            // A real JVM must NOT inherit that: it would load IKVM's incompatible libjava.so and die
            // with "symbol lookup error: ... undefined symbol: JVM_GetInterfaceVersion" (exit 127).
            // Replace it with the JRE's own lib dir (RENZO_SIDECAR_LDPATH, set in the image) so the
            // sidecar's OpenJDK finds the CORRECT libjava.so and libjawt.so (the latter is needed by
            // JCEF). Empty if unset -> the JRE still finds its libs via its own rpath.
            psi.Environment["LD_LIBRARY_PATH"] = Environment.GetEnvironmentVariable("RENZO_SIDECAR_LDPATH") ?? "";

            var logPath = Path.Combine(Path.GetTempPath(), "renzo-sidecar-jvm.log");
            System.IO.StreamWriter? sw = null;
            try { sw = new System.IO.StreamWriter(logPath, append: false) { AutoFlush = true }; } catch { /* logging is best-effort */ }
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data != null) { _logger.LogDebug("[sidecar] {Line}", e.Data); try { sw?.WriteLine(e.Data); } catch { } } };
            proc.ErrorDataReceived += (_, e) => { if (e.Data != null) { _logger.LogDebug("[sidecar] {Line}", e.Data); try { sw?.WriteLine(e.Data); } catch { } } };
            proc.Exited += (_, _) => { _ready = false; _logger.LogWarning("Sidecar process exited (code {Code}); output at {Log}.", SafeExit(proc), logPath); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _proc = proc;
            _logger.LogInformation("Started sidecar JVM (pid {Pid}).", proc.Id);
            return Task.CompletedTask;
        }

        private async Task WaitHealthyAsync(TimeSpan timeout, CancellationToken token)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (_proc is { HasExited: true }) throw new InvalidOperationException("Sidecar process exited during startup.");
                if (await Client.HealthAsync(token).ConfigureAwait(false)) return;
                await Task.Delay(500, token).ConfigureAwait(false);
            }
            throw new TimeoutException("Sidecar did not become healthy in time.");
        }

        private static int SafeExit(Process p) { try { return p.ExitCode; } catch { return -1; } }

        public async ValueTask DisposeAsync()
        {
            try { if (_proc is { HasExited: false }) { _proc.Kill(entireProcessTree: true); await _proc.WaitForExitAsync().ConfigureAwait(false); } }
            catch { /* best effort */ }
            _http.Dispose();
            _startLock.Dispose();
        }
    }
}
