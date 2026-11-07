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
            string className = Runtime.ExtensionClassName.Resolve(entry.Extension.Package, entry.ClassName);

            try
            {
                // Blocking is intentional: IExtensionInterop.Sources is synchronous and interops are
                // created lazily/cached, so this runs once per extension version.
                _client.ConvertAsync(apkPath, _jarPath).GetAwaiter().GetResult();
                var metas = _client.LoadSourcesAsync(_jarPath, className).GetAwaiter().GetResult();
                _sources = metas.Select(m => (ISourceInterop)new SidecarSourceInterop(_client, m)).ToList();
                _logger.LogInformation("Sidecar loaded {Count} source(s) for {Name} v{Version}.", _sources.Count, Name, Version);
                // Write the settings mirror now, not only when someone opens the
                // settings page: coin-site detection reads it from disk.
                if (_sources.Any(s => s.IsConfigurableSource))
                    _ = Task.Run(async () =>
                    {
                        try { await LoadPreferencesAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch (Exception ex) { _logger.LogDebug("Settings for {Name} not read at load: {Msg}", Name, ex.Message); }
                    });
            }
            catch (SidecarException ex)
            {
                _logger.LogError("Sidecar failed to load extension {ClassName} from {Apk}: {Msg}\n{Stack}", className, apkPath, ex.Message, ex.Stack);
                throw;
            }
        }

        // Settings. The values live in the sidecar (the extension's SharedPreferences,
        // settings/source_<id>.xml, the same files the in-process bridge used), so
        // they are the truth here. preferences.json is only a mirror of them, kept
        // for readers on this side (coin-site detection); unlike the in-process
        // bridge, it is never pushed back over the live values, because it can be
        // months old and an extension may have saved something itself since.
        public async Task<List<UniquePreference>> LoadPreferencesAsync(CancellationToken token)
        {
            List<SourcePreference> live = await LiveAsync(token).ConfigureAwait(false);
            await WriteMirrorAsync(live, token).ConfigureAwait(false);
            return PreferenceGrouping.Group(live);
        }

        public async Task SavePreferencesAsync(List<UniquePreference> press, CancellationToken token)
        {
            var wanted = new Dictionary<string, string>();
            foreach (UniquePreference up in press)
                foreach (KeyLanguage kl in up.Languages)
                    wanted[kl.Key] = up.Preference.CurrentValue;

            bool changed = false;
            foreach (SourcePreference sp in await LiveAsync(token).ConfigureAwait(false))
                foreach (KeyPreference p in sp.Preferences)
                    if (wanted.TryGetValue(p.Key, out string? value) && value != null && value != p.CurrentValue)
                    {
                        await _client.SetPreferenceAsync(sp.SourceId, p.Index, value, token).ConfigureAwait(false);
                        changed = true;
                    }
            if (changed)
                await LoadPreferencesAsync(token).ConfigureAwait(false);
        }

        private async Task<List<SourcePreference>> LiveAsync(CancellationToken token)
        {
            var result = new List<SourcePreference>();
            foreach (ISourceInterop s in _sources.Where(s => s.IsConfigurableSource))
            {
                List<KeyPreference> prefs = await _client.PreferencesAsync(s.Id, token).ConfigureAwait(false);
                if (prefs.Count > 0)
                    result.Add(new SourcePreference { SourceId = s.Id, Language = s.Language, Preferences = prefs });
            }
            return result;
        }

        private async Task WriteMirrorAsync(List<SourcePreference> live, CancellationToken token)
        {
            if (live.Count == 0)
                return;
            string path = System.IO.Path.Combine(_structure.GetExtensionFolder(_entry), "preferences.json");
            string json = System.Text.Json.JsonSerializer.Serialize(live);
            if (System.IO.File.Exists(path) && await System.IO.File.ReadAllTextAsync(path, token).ConfigureAwait(false) == json)
                return;
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            // Whole file or nothing: the coin-site scan may be reading it right now.
            string tmp = path + ".tmp";
            await System.IO.File.WriteAllTextAsync(tmp, json, token).ConfigureAwait(false);
            System.IO.File.Move(tmp, path, overwrite: true);
        }

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
