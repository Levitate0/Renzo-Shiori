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

        /// <summary>How often the watchdog probes /health. 0 disables the watchdog.</summary>
        public int WatchdogIntervalSeconds { get; set; } =
            int.TryParse(Environment.GetEnvironmentVariable("RENZO_SIDECAR_WATCHDOG_SECONDS"), out var w) ? w : 15;

        /// <summary>
        /// Consecutive failed probes before a LIVE process is treated as wedged
        /// and killed. Default 4 x 15s = one minute unresponsive, comfortably
        /// inside the 3-minute request timeout callers wait on, and far longer
        /// than any healthy /health call.
        /// </summary>
        public int WatchdogFailuresBeforeRestart { get; set; } =
            int.TryParse(Environment.GetEnvironmentVariable("RENZO_SIDECAR_WATCHDOG_FAILURES"), out var f) ? f : 4;

        /// <summary>Per-probe timeout. /health does nothing but answer, so this is generous.</summary>
        public int WatchdogProbeTimeoutSeconds { get; set; } =
            int.TryParse(Environment.GetEnvironmentVariable("RENZO_SIDECAR_WATCHDOG_PROBE_SECONDS"), out var t) ? t : 10;
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
        private int _generation;
        private readonly CancellationTokenSource _watchdogCts = new();
        private Task? _watchdog;
        private int _consecutiveRestarts;

        /// <summary>
        /// Bumped every time the JVM is (re)started. A restarted sidecar has NO
        /// extensions loaded, so every interop handed out before the restart
        /// refers to a source id the new process has never heard of — calls fail
        /// with "Source &lt;id&gt; not loaded" forever. Anything caching an interop
        /// must compare this against the generation it cached at and drop its
        /// cache when they differ.
        /// </summary>
        public int Generation => Volatile.Read(ref _generation);

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
                int generation = Interlocked.Increment(ref _generation);
                _ready = true;
                _logger.LogInformation("Sidecar ready on 127.0.0.1:{Port} (generation {Generation}).", _opts.Port, generation);
                StartWatchdog();
            }
            finally { _startLock.Release(); }
        }

        /// <summary>
        /// Force-restarts the JVM. Unlike <see cref="EnsureStartedAsync"/> this does
        /// NOT return early when the process is alive — a wedged sidecar is alive,
        /// which is exactly the case that needs killing.
        /// </summary>
        public async Task RestartAsync(string reason, CancellationToken token = default)
        {
            _logger.LogWarning("Restarting sidecar: {Reason}.", reason);
            await _startLock.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _ready = false;
                Process? old = _proc;
                if (old is { HasExited: false })
                {
                    // entireProcessTree: JCEF spawns jcef_helper children, and they
                    // outlive a bare kill of the JVM — that is the helper leak that
                    // has taken this host into swap before.
                    try { old.Kill(entireProcessTree: true); } catch (Exception e) { _logger.LogWarning(e, "Killing the sidecar failed; starting a new one anyway."); }
                    try { await old.WaitForExitAsync(token).ConfigureAwait(false); } catch { /* best effort */ }
                }
                _proc = null;
            }
            finally { _startLock.Release(); }

            // Outside the lock: EnsureStartedAsync takes it itself. It also bumps
            // Generation, which is what makes ExtensionManager drop the interops
            // pointing at sources the new JVM has never loaded.
            await EnsureStartedAsync(token).ConfigureAwait(false);
        }

        private void StartWatchdog()
        {
            if (_opts.WatchdogIntervalSeconds <= 0 || _watchdog is { IsCompleted: false })
                return;
            _watchdog = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token));
        }

        /// <summary>
        /// Keeps the sidecar alive, because nothing else does.
        ///
        /// Two distinct failures, and the second is the one that hurts:
        ///
        ///  * DEAD — the JVM exited. `proc.Exited` only cleared a flag and logged;
        ///    nothing restarted it, so the sidecar stayed down until something
        ///    happened to build a new extension interop. Every source request in
        ///    between failed.
        ///  * WEDGED — the process is alive and accepting connections but never
        ///    answers. Callers then block for the full 3-minute HTTP timeout, one
        ///    after another, and the symptom is not an error: Add Series simply
        ///    reports "No results found" while Browse looks fine, because Browse
        ///    is reading cached rows and never touches the sidecar at all.
        ///
        /// Only the second needs a probe; a wedged process passes every liveness
        /// check there is. Restarting costs a re-load of the extensions actually
        /// used next, which is cheap next to a server that answers nothing.
        /// </summary>
        private async Task WatchdogLoopAsync(CancellationToken token)
        {
            TimeSpan interval = TimeSpan.FromSeconds(_opts.WatchdogIntervalSeconds);
            int failures = 0;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, token).ConfigureAwait(false);

                    // A start/restart is in flight — say nothing, probe nothing.
                    if (_startLock.CurrentCount == 0)
                        continue;

                    if (_proc is null or { HasExited: true })
                    {
                        if (!_ready && _proc is null)
                            continue; // never started, or deliberately disposed
                        failures = 0;
                        await RestartWithBackoffAsync("process is not running", token).ConfigureAwait(false);
                        continue;
                    }

                    if (!_ready)
                        continue; // still coming up

                    using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
                    probe.CancelAfter(TimeSpan.FromSeconds(_opts.WatchdogProbeTimeoutSeconds));
                    bool healthy;
                    try { healthy = await Client.HealthAsync(probe.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!token.IsCancellationRequested) { healthy = false; }

                    if (healthy)
                    {
                        if (failures > 0)
                            _logger.LogInformation("Sidecar answered again after {Failures} failed probe(s).", failures);
                        failures = 0;
                        _consecutiveRestarts = 0;
                        continue;
                    }

                    failures++;
                    _logger.LogWarning("Sidecar /health probe failed ({Failures}/{Limit}).", failures, _opts.WatchdogFailuresBeforeRestart);
                    if (failures >= _opts.WatchdogFailuresBeforeRestart)
                    {
                        failures = 0;
                        await RestartWithBackoffAsync(
                            $"unresponsive for ~{_opts.WatchdogIntervalSeconds * _opts.WatchdogFailuresBeforeRestart}s", token)
                            .ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    // The watchdog must never be the thing that dies.
                    _logger.LogError(e, "Sidecar watchdog iteration failed; continuing.");
                }
            }
        }

        /// <summary>
        /// Restarts, backing off when it keeps happening. A sidecar that cannot
        /// stay up — a bad extension, no memory — would otherwise be relaunched
        /// every minute forever, and each launch costs a JVM and a JCEF init.
        /// </summary>
        private async Task RestartWithBackoffAsync(string reason, CancellationToken token)
        {
            int n = Interlocked.Increment(ref _consecutiveRestarts);
            if (n > 1)
            {
                TimeSpan wait = TimeSpan.FromSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(n - 1, 5))));
                _logger.LogWarning("Sidecar has restarted {Count} times without a healthy period; waiting {Wait} before trying again.", n, wait);
                await Task.Delay(wait, token).ConfigureAwait(false);
            }
            try { await RestartAsync(reason, token).ConfigureAwait(false); }
            catch (Exception e) { _logger.LogError(e, "Sidecar restart failed; the watchdog will try again."); }
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
            // Stop the watchdog FIRST, or it races the shutdown kill below and
            // relaunches a JVM the app is in the middle of tearing down.
            try { _watchdogCts.Cancel(); } catch { /* best effort */ }
            try { if (_watchdog != null) await _watchdog.ConfigureAwait(false); } catch { /* best effort */ }
            _watchdogCts.Dispose();
            try { if (_proc is { HasExited: false }) { _proc.Kill(entireProcessTree: true); await _proc.WaitForExitAsync().ConfigureAwait(false); } }
            catch { /* best effort */ }
            _http.Dispose();
            _startLock.Dispose();
        }
    }
}
