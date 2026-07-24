using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Microsoft.Extensions.Logging;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;

namespace RenzoBackend.Services.Bridge
{
    public class MihonBridgeService : IExtensionManager, IRepositoryManager
    {
        private readonly IBridgeManager _bridgeManager;
        private readonly IWorkingFolderStructure _workingFolderStructure;
        private readonly ILogger _logger;

        private ConcurrentDictionary<string, Lazy<Task<IExtensionInterop>>> extOps = [];
        
        public Task<Preferences> GetPreferencesAsync(CancellationToken cancellationToken) => _bridgeManager.GetPreferencesAsync(cancellationToken);
        public Task SetPreferencesAsync(Preferences prefs, CancellationToken cancellationToken) => _bridgeManager.SetPreferencesAsync(prefs, cancellationToken);

        public MihonBridgeService(ILogger<MihonBridgeService> logger, IBridgeManager bridgeManager, IWorkingFolderStructure workingFolderStructure)
        {

            _logger = logger;
            _bridgeManager = bridgeManager;
            _workingFolderStructure = workingFolderStructure;
        }
        public async Task<T?> MihonErrorWrapperAsync<T>(Func<Task<T>> func, string errorMessage, params object[] pars) where T : class, new()
        {
            try
            {
                return await func().ConfigureAwait(false);
            }
            catch (HttpRequestException httpEx)
            {
                object[] pars2 = pars.ToArray();
                Array.Resize(ref pars2, pars2.Length + 1);
                pars2[^1] = httpEx.StatusCode ?? HttpStatusCode.InternalServerError;
                _logger.LogError(errorMessage + " Http Error: {httperror}", pars2);
                return null;
            }
            catch (TaskCanceledException)
            {
                _logger.LogError(errorMessage + " Task was cancelled", pars);
                return null;
            }
            catch (OperationCanceledException)
            {
                _logger.LogError(errorMessage + " Operation was cancelled", pars);
                return null;
            }
            catch (Exception ex)
            {
                // IKVM-thrown Java exceptions surface with no .NET-visible stack —
                // a bare "java.lang.NullPointerException" line is undiagnosable.
                // Pull the Java-side stack (via reflection, so this assembly needs
                // no IKVM reference) so extension failures show WHERE they broke.
                string javaStack = ExtractJavaStack(ex);
                if (javaStack.Length > 0)
                {
                    object[] pars2 = pars.ToArray();
                    Array.Resize(ref pars2, pars2.Length + 1);
                    pars2[^1] = javaStack;
                    _logger.LogError(ex, errorMessage + " {javaStack}", pars2);
                }
                else
                    _logger.LogError(ex, errorMessage, pars);
                return null;
            }
        }

        /// <summary>Java-side stack frames of an IKVM Throwable, or "" when unavailable.</summary>
        private static string ExtractJavaStack(Exception ex)
        {
            try
            {
                var m = ex.GetType().GetMethod("getStackTrace", Type.EmptyTypes);
                if (m == null || m.Invoke(ex, null) is not Array frames || frames.Length == 0)
                    return "";
                var sb = new System.Text.StringBuilder("\nJava stack:");
                int n = 0;
                foreach (object? f in frames)
                {
                    if (f == null) continue;
                    sb.Append("\n  at ").Append(f);
                    if (++n >= 14) break;
                }
                return sb.ToString();
            }
            catch { return ""; }
        }

        // ── Extension version management (pin / rollback / sideload) ────────
        // The resilience layer for source extensions that break on update: hold a
        // known-good version (pin), switch between installed versions (rollback),
        // or install a patched APK directly (sideload) — auto-update never
        // replaces a pinned group.

        public List<RepositoryGroup> ListLocalExtensions() =>
            _bridgeManager.LocalExtensionManager.ListExtensions();

        public async Task<RepositoryGroup> SetExtensionVersionAsync(string name, string version, CancellationToken token = default)
        {
            RepositoryGroup group = _bridgeManager.LocalExtensionManager.FindExtension(name)
                ?? throw new KeyNotFoundException($"Extension '{name}' not found");
            int idx = group.Entries.FindIndex(e => e.Extension.Version == version);
            if (idx < 0)
                throw new KeyNotFoundException($"Version '{version}' is not installed for '{name}'");
            string latest = group.Entries.OrderByDescending(e => e.Extension.VersionCode).First().Extension.Version;
            // Choosing an older version pins the group so the hourly auto-update
            // can't immediately replace it again.
            if (version != latest)
                group.AutoUpdate = false;
            group.ActiveEntry = idx;
            group = await _bridgeManager.LocalExtensionManager.SetActiveExtensionVersionAsync(group, token).ConfigureAwait(false);
            extOps.TryRemove(name, out _); // drop the stale cached interop
            return group;
        }

        public async Task<RepositoryGroup> SetExtensionAutoUpdateAsync(string name, bool enabled, CancellationToken token = default)
        {
            RepositoryGroup group = _bridgeManager.LocalExtensionManager.FindExtension(name)
                ?? throw new KeyNotFoundException($"Extension '{name}' not found");
            group.AutoUpdate = enabled;
            if (enabled)
            {
                // Un-pinning re-activates the newest installed version.
                var lastEntry = group.Entries.OrderByDescending(e => e.Extension.VersionCode).First();
                group.ActiveEntry = group.Entries.IndexOf(lastEntry);
            }
            group = await _bridgeManager.LocalExtensionManager.SetActiveExtensionVersionAsync(group, token).ConfigureAwait(false);
            extOps.TryRemove(name, out _);
            return group;
        }

        public async Task<RepositoryGroup?> SideloadExtensionAsync(byte[] apk, CancellationToken token = default)
        {
            RepositoryGroup? group = await _bridgeManager.LocalExtensionManager.AddExtensionAsync(apk, true, token).ConfigureAwait(false);
            if (group == null)
                return null;
            // The sideloaded entry is appended last; make it active and pin the
            // group so auto-update can't replace the manual build.
            group.ActiveEntry = group.Entries.Count - 1;
            group.AutoUpdate = false;
            group = await _bridgeManager.LocalExtensionManager.SetActiveExtensionVersionAsync(group, token).ConfigureAwait(false);
            extOps.TryRemove(group.Name, out _);
            return group;
        }

        private async Task<IExtensionInterop> GetFromNameAsync(string name, CancellationToken token = default)
        {
            Lazy<Task<IExtensionInterop>> value = extOps.GetOrAdd(name, (nam) =>
            {
                var allLocal = _bridgeManager.LocalExtensionManager.ListExtensions();
                var repo = allLocal.FirstOrDefault(a => a.Name.Equals(nam, StringComparison.OrdinalIgnoreCase));
                if (repo == null)
                    throw new InvalidOperationException($"Extension '{nam}' not found");
                return new Lazy<Task<IExtensionInterop>>(_bridgeManager.LocalExtensionManager.GetInteropAsync(repo, token));
            });
            return await value.Value.ConfigureAwait(false);
        }
        private async Task<IExtensionInterop> GetFromPackageAsync(string package, CancellationToken token = default)
        {
            var allLocal = _bridgeManager.LocalExtensionManager.ListExtensions();
            var repo = allLocal.FirstOrDefault(a => a.GetActiveEntry().Extension.Package.Equals(package, StringComparison.OrdinalIgnoreCase));
            if (repo==null)
            {
                throw new InvalidOperationException("Package not found");
            }
            Lazy<Task<IExtensionInterop>> value = extOps.GetOrAdd(repo.Name, (nam) =>
            {
                var allLocal = _bridgeManager.LocalExtensionManager.ListExtensions();
                var repo = allLocal.FirstOrDefault(a => a.Name.Equals(nam, StringComparison.OrdinalIgnoreCase));
                if (repo == null)
                    throw new InvalidOperationException($"Extension '{nam}' not found");
                return new Lazy<Task<IExtensionInterop>>(_bridgeManager.LocalExtensionManager.GetInteropAsync(repo, token));
            });
            return await value.Value.ConfigureAwait(false);
        }
        private async Task<ISourceInterop> GetFromNameAndSourceAsync(string nameandsource, CancellationToken token = default)
        {
            string[] split = nameandsource.Split("|");
            if (split.Length < 2)
                throw new InvalidOperationException("Invalid Name And Source");
            long source = 0;
            if (!long.TryParse(split[1], out source))
                throw new InvalidOperationException("Invalid Source Id");
            string name = split[0];
            IExtensionInterop extOp = await GetFromNameAsync(name, token).ConfigureAwait(false);
            if (extOp == null)
                throw new InvalidOperationException($"Extension '{name}' not found for source '{source}'");
            ISourceInterop? src = extOp.Sources.FirstOrDefault(a => a.Id == source);
            if (src == null)
                throw new InvalidOperationException($"Source '{source}' not found in extension '{name}'");
            return src!;
        }
        private async Task<ISourceInterop> GetFromMihonProviderIdAsync(string mihonproviderId, CancellationToken token = default)
        {
            string[] split = mihonproviderId.Split("|");
            if (split.Length < 2)
                throw new InvalidOperationException("Invalid Package And Source");
            long source = 0;
            if (!long.TryParse(split[1], out source))
                throw new InvalidOperationException("Invalid Source Id");
            string package = split[0];
            IExtensionInterop extOp = await GetFromPackageAsync(package, token).ConfigureAwait(false);
            if (extOp == null)
                throw new InvalidOperationException($"Extension '{package}' not found for source '{source}'");
            ISourceInterop? src = extOp.Sources.FirstOrDefault(a => a.Id == source);
            if (src == null)
                throw new InvalidOperationException($"Source '{source}' not found in extension '{package}'");
            return src!;
        }


        public Task<ISourceInterop> SourceFromProviderIdAsync(string mihonProviderName, CancellationToken token = default)
        {
            return GetFromMihonProviderIdAsync(mihonProviderName, token);
        }

        public Task<RepositoryGroup?> AddExtensionAsync(TachiyomiExtension extension, bool force = false, CancellationToken token = default)
        {
            return _bridgeManager.LocalExtensionManager.AddExtensionAsync(extension, force, token);
        }

        public Task<RepositoryGroup?> AddExtensionAsync(TachiyomiRepository repository, TachiyomiExtension extension, bool force = false, CancellationToken token = default)
        {
            return _bridgeManager.LocalExtensionManager.AddExtensionAsync(repository, extension, force, token);
        }

        public Task<RepositoryGroup?> AddExtensionAsync(byte[] apk, bool force = false, CancellationToken token = default)
        {
            return _bridgeManager.LocalExtensionManager.AddExtensionAsync(apk, force, token);
        }

        public Task<IExtensionInterop> GetInteropAsync(RepositoryGroup entry, CancellationToken token = default)
        {
            return GetFromNameAsync(entry.Name, token);
        }

        public List<RepositoryGroup> ListExtensions()
        {
            return _bridgeManager.LocalExtensionManager.ListExtensions();
        }

        public RepositoryGroup? FindExtension(string name)
        {
            return _bridgeManager.LocalExtensionManager.FindExtension(name);
        }

        public Task<bool> RemoveExtensionAsync(RepositoryGroup group, CancellationToken token = default)
        {
            return _bridgeManager.LocalExtensionManager.RemoveExtensionAsync(group, token);
        }

        public Task<RepositoryGroup?> RemoveExtensionVersionAsync(RepositoryEntry entry, CancellationToken token = default)
        {
            return _bridgeManager.LocalExtensionManager.RemoveExtensionVersionAsync(entry, token);
        }

        public Task<RepositoryGroup> SetActiveExtensionVersionAsync(RepositoryGroup group, CancellationToken token = default)
        {
            return _bridgeManager.LocalExtensionManager.SetActiveExtensionVersionAsync(group, token);
        }

        public Task<TachiyomiRepository> AddOnlineRepositoryAsync(TachiyomiRepository repository, CancellationToken token = default)
        {
            return _bridgeManager.OnlineRepositoryManager.AddOnlineRepositoryAsync(repository, token);
        }

        public List<TachiyomiRepository> ListOnlineRepositories()
        {
            return _bridgeManager.OnlineRepositoryManager.ListOnlineRepositories();
        }

        public Task RefreshAllRepositoriesAsync(CancellationToken token = default)
        {
            return _bridgeManager.OnlineRepositoryManager.RefreshAllRepositoriesAsync(token);
        }

        public Task<bool> RemoveOnlineRespositoryAsync(TachiyomiRepository repository, CancellationToken token = default)
        {
            return _bridgeManager.OnlineRepositoryManager.RemoveOnlineRespositoryAsync(repository, token);
        }
    }
}
