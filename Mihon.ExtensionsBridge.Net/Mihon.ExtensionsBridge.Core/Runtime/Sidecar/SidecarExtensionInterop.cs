using Microsoft.Extensions.Logging;
using Mihon.ExtensionsBridge.Core.Abstractions;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;

namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>
    /// Drop-in replacement for <c>JarExtensionInterop</c> that loads an extension through the JVM
    /// sidecar instead of IKVM. It hands the original APK to the sidecar's /convert (dex2jar +
    /// enjarify + signature-merge) and /load, and exposes the resulting sources as
    /// <see cref="SidecarSourceInterop"/>. Same <see cref="IInternalExtensionInterop"/> surface, so
    /// the ExtensionManager and everything above it are unchanged.
    /// </summary>
    public sealed class SidecarExtensionInterop : IInternalExtensionInterop
    {
        private readonly ILogger _logger;
        private readonly IWorkingFolderStructure _structure;
        private readonly RepositoryEntry _entry;
        private readonly SidecarClient _client;
        private readonly string _jarPath;
        private List<ISourceInterop> _sources = new();

        public string Name { get; }
        public string Version { get; set; }
        public string Id => _entry.Id;
        public List<ISourceInterop> Sources => _sources;

        public SidecarExtensionInterop(IWorkingFolderStructure structure, RepositoryEntry entry, ILogger logger, SidecarClient client, string? optionalTempPath = null)
        {
            _structure = structure ?? throw new ArgumentNullException(nameof(structure));
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            Name = entry.Name;
            Version = entry.Extension.Version;

            var baseDir = !string.IsNullOrEmpty(optionalTempPath) ? optionalTempPath! : _structure.GetExtensionVersionFolder(_entry);
            var apkPath = System.IO.Path.Combine(baseDir, entry.Apk.FileName);
            if (!System.IO.File.Exists(apkPath))
                throw new System.IO.FileNotFoundException("APK not found for sidecar load.", apkPath);
            // Convert next to the APK; the sidecar shares this filesystem (same container).
            _jarPath = System.IO.Path.Combine(baseDir, System.IO.Path.GetFileNameWithoutExtension(entry.Apk.FileName) + ".sidecar.jar");
            string className = entry.Extension.Package + entry.ClassName;

            try
            {
                // Blocking is intentional: IExtensionInterop.Sources is synchronous and interops are
                // created lazily/cached, so this runs once per extension version.
                _client.ConvertAsync(apkPath, _jarPath).GetAwaiter().GetResult();
                var metas = _client.LoadSourcesAsync(_jarPath, className).GetAwaiter().GetResult();
                _sources = metas.Select(m => (ISourceInterop)new SidecarSourceInterop(_client, m)).ToList();
                _logger.LogInformation("Sidecar loaded {Count} source(s) for {Name} v{Version}.", _sources.Count, Name, Version);
            }
            catch (SidecarException ex)
            {
                _logger.LogError("Sidecar failed to load extension {ClassName} from {Apk}: {Msg}\n{Stack}", className, apkPath, ex.Message, ex.Stack);
                throw;
            }
        }

        // Preferences via the sidecar are being completed; empty for now (sources still work).
        public Task<List<UniquePreference>> LoadPreferencesAsync(CancellationToken token) => Task.FromResult(new List<UniquePreference>());
        public Task SavePreferencesAsync(List<UniquePreference> press, CancellationToken token) => Task.CompletedTask;

        public Task ShutdownAsync(CancellationToken token)
        {
            _sources = new();
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            try { _client.UnloadAsync(_jarPath).GetAwaiter().GetResult(); } catch { /* best effort */ }
            _sources = new();
        }
    }
}
