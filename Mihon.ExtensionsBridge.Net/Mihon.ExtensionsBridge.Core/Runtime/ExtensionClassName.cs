namespace Mihon.ExtensionsBridge.Core.Runtime
{
    /// <summary>
    /// Resolves an extension's main class from its APK manifest metadata.
    ///
    /// <c>tachiyomi.extension.class</c> may hold either form, and Mihon's own
    /// ExtensionLoader distinguishes them:
    ///
    /// <code>
    /// val className = if (it.startsWith(".")) pkgName + it else it
    /// </code>
    ///
    /// RELATIVE (leading dot) — ".ExtensionGenerated" — is prefixed with the package.
    /// ABSOLUTE — "keiyoushi.source.Generated" — is used verbatim.
    ///
    /// Both call sites used to concatenate unconditionally, which silently corrupted
    /// every absolute name by gluing the package onto the front:
    ///
    ///   eu.kanade.tachiyomi.extension.en.arenascans + keiyoushi.source.Generated
    ///   = eu.kanade.tachiyomi.extension.en.arenascanskeiyoushi.source.Generated
    ///
    /// which throws ClassNotFoundException at /sources/load. That is not hypothetical:
    /// keiyoushi moved its generated extensions to the absolute
    /// <c>keiyoushi.source.Generated</c> class, so on 2026-09-16 roughly fifteen
    /// sources — Arena Scans, AllAnime, Asura Scans, Comic Asura, Galaxy Manga,
    /// King of Shojo, MangaKatana, Philia, Shojo Scans and more — failed to load on
    /// every single update cycle. Because the install never completed, the updater
    /// re-downloaded them forever.
    ///
    /// The value is treated as ONE class name. The sidecar's loadExtensionSources
    /// calls Class.forName on whatever it is handed and never splits, so a
    /// multi-class list was unsupported before this and still is — deliberately not
    /// changed here, since inventing a split would only move the failure.
    /// </summary>
    public static class ExtensionClassName
    {
        public static string Resolve(string? package, string? className)
        {
            if (string.IsNullOrWhiteSpace(className))
                return package ?? string.Empty;

            var name = className.Trim();
            return name.StartsWith('.') ? (package ?? string.Empty) + name : name;
        }
    }
}
