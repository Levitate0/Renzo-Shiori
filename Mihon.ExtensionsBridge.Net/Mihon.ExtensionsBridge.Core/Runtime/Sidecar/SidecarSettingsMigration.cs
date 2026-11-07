using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>
    /// Carries extension settings across from the in-process bridge, once. The two
    /// keep SharedPreferences in different folders (android/settings and
    /// android/sidecar/settings), and nothing copied them at the cutover, so every
    /// extension quietly fell back to its defaults: MangaDex lost its content
    /// ratings and alt titles, coin sites lost "show paid chapters", tag
    /// blocklists vanished (found 2026-11-07, 400 values).
    ///
    /// Only keys the sidecar has no value for are copied, so nothing it saved
    /// since is touched. Runs before the JVM starts, while no one holds the files.
    /// </summary>
    public static class SidecarSettingsMigration
    {
        private const string Marker = ".migrated-from-inprocess";

        // Extension-managed caches, not settings: an old copy can only be stale.
        private static readonly string[] SkipKeyParts = { "slug_map", "cache" };

        public static void Run(string androidFolder, string dataRoot, ILogger logger)
        {
            string from = Path.Combine(androidFolder, "settings");
            string to = Path.Combine(dataRoot, "settings");
            string marker = Path.Combine(to, Marker);
            if (File.Exists(marker) || !Directory.Exists(from) || Path.GetFullPath(from) == Path.GetFullPath(to))
                return;
            Directory.CreateDirectory(to);
            int files = 0, values = 0;
            foreach (string oldFile in Directory.EnumerateFiles(from, "source_*.xml"))
            {
                try
                {
                    string newFile = Path.Combine(to, Path.GetFileName(oldFile));
                    Dictionary<string, string> old = Read(oldFile);
                    Dictionary<string, string> current = File.Exists(newFile) ? Read(newFile) : new();
                    int added = 0;
                    foreach (var group in old.GroupBy(kv => SetName(kv.Key)))
                    {
                        if (SkipKeyParts.Any(p => group.Key.Contains(p, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        // A string set is stored as name.size + name.0..n: copy it whole or not at all.
                        if (current.Keys.Any(k => SetName(k) == group.Key))
                            continue;
                        foreach (var kv in group)
                        {
                            current[kv.Key] = kv.Value;
                            added++;
                        }
                    }
                    if (added == 0)
                        continue;
                    Write(newFile, current);
                    files++;
                    values += added;
                }
                catch (Exception ex)
                {
                    logger.LogWarning("Extension settings in {File} not carried over: {Msg}", Path.GetFileName(oldFile), ex.Message);
                }
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));
            logger.LogInformation("Carried {Values} extension setting value(s) in {Files} file(s) over from the in-process bridge.", values, files);
        }

        /// <summary>"tags.3" and "tags.size" belong to set "tags"; any other key is its own group.</summary>
        private static string SetName(string key)
        {
            int dot = key.LastIndexOf('.');
            if (dot <= 0)
                return key;
            string tail = key[(dot + 1)..];
            return tail == "size" || tail.All(char.IsDigit) ? key[..dot] : key;
        }

        private static Dictionary<string, string> Read(string file)
        {
            var doc = XDocument.Load(file);
            return doc.Root!.Elements("entry")
                .Where(e => e.Attribute("key") != null)
                .ToDictionary(e => e.Attribute("key")!.Value, e => e.Value);
        }

        // Java's Properties.storeToXML shape, which loadFromXML reads back.
        private static void Write(string file, Dictionary<string, string> values)
        {
            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                new XDocumentType("properties", null, "http://java.sun.com/dtd/properties.dtd", null),
                new XElement("properties", values.Select(kv => new XElement("entry", new XAttribute("key", kv.Key), kv.Value))));
            string tmp = file + ".tmp";
            doc.Save(tmp);
            File.Move(tmp, file, overwrite: true);
        }
    }
}
