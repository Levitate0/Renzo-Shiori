using extension.bridge;
using eu.kanade.tachiyomi.source;
using java.io;
using java.lang;
using java.net;
using Microsoft.Extensions.Logging;
using System.Reflection;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Models.Extensions;
using static kotlin.reflect.jvm.@internal.ReflectProperties;
using Mihon.ExtensionsBridge.Core.Abstractions;

namespace Mihon.ExtensionsBridge.Core.Runtime
{
    public class JarExtensionInterop : IInternalExtensionInterop
    {
        private readonly ILogger _logger;
        private readonly IWorkingFolderStructure _structure;
        private readonly RepositoryEntry _entry;
        private readonly string _jarPath;
        
        private URLClassLoader? _classLoader;
        private List<ISourceInterop> _sources = new();
        private readonly CancellationTokenSource _shutdownCts = new();
        private readonly List<IDisposable> _disposables = new();

        public string Name { get; }
        public string Version { get; set; }
        public List<ISourceInterop> Sources => _sources;

        public string Id => _entry.Id;



        public JarExtensionInterop(IWorkingFolderStructure structure, RepositoryEntry entry, ILogger logger, string? optionalTempPath = null)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _structure = structure ?? throw new ArgumentNullException(nameof(structure));
            _entry = entry ?? throw new ArgumentNullException(nameof(entry));

            if (string.IsNullOrWhiteSpace(entry.Jar.FileName))
                throw new ArgumentException("JAR file name is required.", nameof(entry));
            if (string.IsNullOrWhiteSpace(structure.ExtensionsFolder))
                throw new ArgumentException("Extensions folder path is required.", nameof(structure));

            string jarPath;
            if (!string.IsNullOrEmpty(optionalTempPath))
                jarPath = System.IO.Path.Combine(optionalTempPath, entry.Jar.FileName);
            else
                jarPath = System.IO.Path.Combine(_structure.GetExtensionVersionFolder(_entry), entry.Jar.FileName);

            if (!System.IO.File.Exists(jarPath))
                throw new System.IO.FileNotFoundException("Jar file not found.", jarPath);
            _jarPath = jarPath;
            Name = entry.Name;
            Version = entry.Extension.Version;
            string className = entry.Extension.Package + entry.ClassName;
            var list = new List<ISourceInterop>();
            // Load the extension's main class through a child-first loader whose PARENT is the
            // compat runtime's own classloader (the assembly that owns eu.kanade.tachiyomi.source.*).
            //
            // This is deliberately NOT the Kotlin `Extensions.loadExtensionSources`, which builds a
            // ChildFirstURLClassLoader with a null parent and relies on getSystemClassLoader() to
            // resolve the source-api types. Under IKVM that path resolves the source-api through a
            // different classloader than the extension's own classes, which — for extensions-lib 1.6
            // sources (the generated `ExtensionGenerated : <obfuscated HttpSource subclass>` shape) —
            // makes IKVM's eager linker see the HttpSource hierarchy through two loaders and throw a
            // messageless java.lang.IncompatibleClassChangeError at construction. Anchoring the parent
            // to the compat classloader keeps every source-api type single-identity, so both current
            // (1.4/1.5) and 1.6+ extensions link and instantiate. Version-agnostic by construction:
            // it makes no assumptions about the extension's ext-lib version.
            var jarUrl = new java.net.URL(new java.io.File(jarPath).toURI().toURL().toString());
            var loader = new extension.bridge.ChildFirstURLClassLoader(new java.net.URL[] { jarUrl }, MiscExtensions.ClassLoader);
            _classLoader = loader;
            try
            {
                var classToLoad = java.lang.Class.forName(className, false, loader);
                object instance = classToLoad.getDeclaredConstructor(new java.lang.Class[0]).newInstance(new object[0]);
                if (instance is SourceFactory sf)
                {
                    foreach (var o in sf.createSources().toArray())
                        list.Add(new SourceInterop((Source)o, logger));
                }
                else if (instance is Source s)
                {
                    list.Add(new SourceInterop(s, logger));
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Class {className} is neither a SourceFactory nor a Source implementation (got {instance?.GetType().FullName ?? "null"}).");
                }
            }
            catch (java.lang.Throwable jt)
            {
                // Extension instantiation runs the source constructor reflectively; failures surface
                // as java.lang.reflect.InvocationTargetException whose real cause is otherwise dropped
                // by the default logging (Throwable.toString() omits the cause chain). Unwrap and log
                // the full Java cause chain (plus CLR detail) so load failures stay diagnosable.
                _logger.LogError("Failed to load extension sources for {ClassName} from {Jar}. Full Java cause chain:\n{Trace}",
                    className, jarPath, DescribeJavaThrowable(jt));
                throw;
            }
            _sources = list;
        }

        /// <summary>
        /// Renders a Java <see cref="java.lang.Throwable"/> and its full <c>getCause()</c> chain
        /// (including reflective <c>InvocationTargetException</c> wrappers) to a string, so the real
        /// root cause of an extension-load failure is visible in the logs.
        /// </summary>
        internal static string DescribeJavaThrowable(java.lang.Throwable jt)
        {
            try
            {
                var sw = new java.io.StringWriter();
                var pw = new java.io.PrintWriter(sw);
                java.lang.Throwable? cur = jt;
                int depth = 0;
                var seen = new HashSet<java.lang.Throwable>();
                while (cur != null && seen.Add(cur) && depth++ < 12)
                {
                    pw.println((depth == 1 ? "" : "Caused by: ") + cur.getClass().getName() + ": " + cur.getMessage());
                    var st = cur.getStackTrace();
                    for (int i = 0; i < st.Length && i < 15; i++)
                        pw.println("    at " + st[i].toString());
                    // IKVM leaves getMessage() empty for linkage errors, but the CLR-level
                    // exception detail (Message + internal IKVM.Runtime frames) identifies the
                    // exact failing member/check. Surface it for the deepest cause.
                    System.Exception clr = cur;
                    pw.println("  [CLR] " + clr.GetType().FullName + ": " + clr.Message);
                    if (!string.IsNullOrEmpty(clr.StackTrace))
                    {
                        var clrFrames = clr.StackTrace.Split('\n');
                        for (int i = 0; i < clrFrames.Length && i < 12; i++)
                            pw.println("  [CLR]   " + clrFrames[i].TrimEnd());
                    }
                    // InvocationTargetException hides the real cause behind getTargetException().
                    if (cur is java.lang.reflect.InvocationTargetException ite && ite.getTargetException() != null)
                        cur = ite.getTargetException() as java.lang.Throwable;
                    else
                        cur = cur.getCause() as java.lang.Throwable;
                }
                pw.flush();
                return sw.toString();
            }
            catch (System.Exception ex)
            {
                return "<failed to describe Java throwable: " + ex.Message + ">";
            }
        }

        // Preferences (same implementation pattern as ExtensionInterop)
        public async Task<List<UniquePreference>> LoadPreferencesAsync(CancellationToken token)
        {
            var sourcePrefs = await SyncSourcePreferencesAsync(token).ConfigureAwait(false);
            sourcePrefs = SortPreferencesByLanguageFallback(sourcePrefs);
            Dictionary<string, UniquePreference> uniquePrefs = new Dictionary<string, UniquePreference>();
            foreach (var sp in sourcePrefs)
            {
                foreach (var p in sp.Preferences)
                {
                    string uniqueKey = p.Key;
                    int lastUnderscore = p.Key.LastIndexOf('_');
                    string possibleEnd = "_" + sp.Language;
                    if (uniqueKey.EndsWith(possibleEnd))
                    {
                        uniqueKey = uniqueKey.Substring(0, uniqueKey.Length - possibleEnd.Length);
                    }
                    else if (lastUnderscore > 1)
                    {
                        uniqueKey = uniqueKey.Substring(0, lastUnderscore);
                    }
                    if (uniquePrefs.ContainsKey(uniqueKey))
                    {
                        uniquePrefs[uniqueKey].Languages.Add(new KeyLanguage { Key = p.Key, Language = sp.Language });
                        continue;
                    }
                    UniquePreference up = new UniquePreference
                    {
                        Languages = new List<KeyLanguage> { new KeyLanguage { Key = p.Key, Language = sp.Language } },
                        Preference = p,
                    };
                    uniquePrefs.Add(uniqueKey, up);
                }
            }
            return uniquePrefs.Values.ToList();
        }

        public async Task SavePreferencesAsync(List<UniquePreference> press, CancellationToken token)
        {
            string prefs = GetPreferencesFilePath(_entry);
            List<SourcePreference> existing = new List<SourcePreference>();
            if (!System.IO.File.Exists(prefs))
                return;
            string json = await System.IO.File.ReadAllTextAsync(prefs, token).ConfigureAwait(false);
            existing = System.Text.Json.JsonSerializer.Deserialize<List<SourcePreference>>(json) ?? new List<SourcePreference>();
            bool change = false;
            Dictionary<string, UniquePreference> keyDict = new Dictionary<string, UniquePreference>();
            foreach (UniquePreference up in press)
            {
                foreach (KeyLanguage kl in up.Languages)
                {
                    keyDict[kl.Key] = up;
                }
            }

            foreach (SourcePreference sp in existing)
            {
                foreach (KeyPreference p in sp.Preferences)
                {
                    if (keyDict.ContainsKey(p.Key))
                    {
                        Preference pr = keyDict[p.Key].Preference;
                        if (p.CurrentValue != pr.CurrentValue)
                        {
                            p.CurrentValue = pr.CurrentValue;
                            change = true;
                        }
                    }
                }
            }
            if (change)
            {
                await SaveInternalSourcePreferencesAsync(existing, token).ConfigureAwait(false);
                await SyncSourcePreferencesAsync(token).ConfigureAwait(false);
            }
        }

        public Task ShutdownAsync(CancellationToken token)
        {
            try
            {
                _shutdownCts.Cancel();
                foreach (var d in _disposables)
                {
                    try { d.Dispose(); } catch { }
                }
                _disposables.Clear();
                if (!string.IsNullOrWhiteSpace(_jarPath))
                {
                    try { extension.bridge.StartupKt.unloadExtension(_jarPath); } catch (System.Exception ex) { _logger.LogWarning(ex, "Error unloading extension classloader"); }
                }
            }
            catch { }
            return Task.CompletedTask;
        }

        private int _disposeState = 0; // 0 = not disposed, 1 = disposing, 2 = disposed

        public void Dispose()
        {
            // Ensure Dispose executes only once and is not re-entrant
            if (System.Threading.Interlocked.CompareExchange(ref _disposeState, 1, 0) != 0)
            {
                return;
            }

            try
            {
                try { _shutdownCts.Cancel(); } catch { }

                foreach (var d in _disposables)
                {
                    try { d.Dispose(); } catch { }
                }
                _disposables.Clear();

                if (!string.IsNullOrWhiteSpace(_jarPath))
                {
                    try { extension.bridge.StartupKt.unloadExtension(_jarPath); } catch (System.Exception ex) { _logger.LogWarning(ex, "Error unloading extension classloader"); }
                }

                // Close and release the classloader last, after all interop objects are disposed
                var cl = _classLoader;
                _classLoader = null;
                if (cl != null)
                {
                    try
                    {
                        cl.close();
                    }
                    catch (System.Exception ex)
                    {
                        _logger.LogWarning(ex, "Error closing URLClassLoader");
                    }
                }
            }
            catch (System.Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing JarExtensionInterop");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _disposeState, 2);
            }
        }


        // Preferences helpers (same as ExtensionInterop but using extension folder path)
        private string GetPreferencesFilePath(RepositoryEntry entry)
        {
            string preFile = _structure.GetExtensionFolder(_entry);
            return System.IO.Path.Combine(preFile, "preferences.json");
        }

        private async Task SaveInternalSourcePreferencesAsync(List<SourcePreference> press, CancellationToken token)
        {
            string prefs = GetPreferencesFilePath(_entry);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(prefs)!);
            string updatedJson = System.Text.Json.JsonSerializer.Serialize(press);
            await System.IO.File.WriteAllTextAsync(prefs, updatedJson, token).ConfigureAwait(false);
        }

        private async Task<List<SourcePreference>> SyncSourcePreferencesAsync(CancellationToken token)
        {
            string prefs = GetPreferencesFilePath(_entry);
            var existing = new List<SourcePreference>();
            if (System.IO.File.Exists(prefs))
            {
                var json = await System.IO.File.ReadAllTextAsync(prefs, token).ConfigureAwait(false);
                existing = System.Text.Json.JsonSerializer.Deserialize<List<SourcePreference>>(json) ?? new List<SourcePreference>();
            }
            var extprefs = new List<SourcePreference>();
            foreach (var source in _sources)
            {
                var sourcePrefs = source.GetPreferences();
                if (sourcePrefs != null)
                {
                    extprefs.Add(new SourcePreference { SourceId = source.Id, Language = source.Language, Preferences = sourcePrefs });
                }
            }
            bool needsSave = false;
            foreach (var extPref in extprefs)
            {
                var match = existing.Find(e => e.SourceId == extPref.SourceId);
                if (match == null)
                {
                    existing.Add(extPref);
                    needsSave = true;
                    continue;
                }
                var source = _sources.First(s => s.Id == extPref.SourceId);
                foreach (var p in extPref.Preferences)
                {
                    var match2 = match.Preferences.FirstOrDefault(pr => pr.Key == p.Key);
                    if (match2 == null)
                    {
                        match.Preferences.Add(p);
                        match.Preferences = match.Preferences.OrderBy(pr => pr.Index).ToList();
                        needsSave = true;
                    }
                    else if (match2.CurrentValue != p.CurrentValue)
                    {
                        source.SetPreference(p.Index, match2.CurrentValue);
                    }
                }
            }
            if (needsSave)
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(prefs)!);
                string updatedJson = System.Text.Json.JsonSerializer.Serialize(existing);
                await System.IO.File.WriteAllTextAsync(prefs, updatedJson, token).ConfigureAwait(false);
            }
            return existing;
        }

        private static List<SourcePreference> SortPreferencesByLanguageFallback(List<SourcePreference> prefs)
        {
            string[] languageFallbackOrder = new[] { "en", "es", "fr", "de", "it", "pt", "ru", "ja", "zh" };
            return prefs.OrderBy(p =>
            {
                int index = Array.IndexOf(languageFallbackOrder, p.Language);
                return index >= 0 ? index : int.MaxValue;
            }).ThenBy(p => p.Language).ToList();
        }
    }
}

