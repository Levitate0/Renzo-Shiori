using Microsoft.Extensions.Logging.Abstractions;
using Mihon.ExtensionsBridge.Core.Runtime.Sidecar;
using System.Xml.Linq;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// Extension settings moved folders at the sidecar cutover and nothing copied
/// them, so every extension ran on defaults (2026-11-07). The carry-over must
/// fill only what the sidecar lacks and never touch what it saved since.
/// </summary>
public class SidecarSettingsMigrationTests
{
    [Fact]
    public void Copies_only_missing_settings_and_runs_once()
    {
        string root = Path.Combine(Path.GetTempPath(), "rz-settings-" + Guid.NewGuid().ToString("N"));
        string android = Path.Combine(root, "android"), data = Path.Combine(android, "sidecar");
        Directory.CreateDirectory(Path.Combine(android, "settings"));
        Directory.CreateDirectory(Path.Combine(data, "settings"));
        try
        {
            Save(Path.Combine(android, "settings", "source_1.xml"), new()
            {
                ["show_paid"] = "true",
                ["mirror"] = "old.example",
                ["rating.size"] = "2", ["rating.0"] = "safe", ["rating.1"] = "erotica",
                ["tags.size"] = "1", ["tags.0"] = "yaoi",
                ["slug_map_v2"] = "{\"a\":\"a-1\"}",
            });
            Save(Path.Combine(data, "settings", "source_1.xml"), new()
            {
                ["mirror"] = "new.example",          // saved by the sidecar since: keep
                ["tags.size"] = "1", ["tags.0"] = "bl", // a set it has: never merged into
            });
            Save(Path.Combine(android, "settings", "source_2.xml"), new() { ["adult"] = "false" });

            SidecarSettingsMigration.Run(android, data, NullLogger.Instance);

            var one = Load(Path.Combine(data, "settings", "source_1.xml"));
            Assert.Equal("true", one["show_paid"]);
            Assert.Equal("new.example", one["mirror"]);
            Assert.Equal(new[] { "erotica", "safe" }, new[] { one["rating.0"], one["rating.1"] }.Order());
            Assert.Equal("2", one["rating.size"]);
            Assert.Equal("bl", one["tags.0"]);
            Assert.Equal("1", one["tags.size"]);
            Assert.False(one.ContainsKey("slug_map_v2"));
            Assert.Equal("false", Load(Path.Combine(data, "settings", "source_2.xml"))["adult"]);

            // Once only: a setting the user clears later is not brought back.
            File.Delete(Path.Combine(data, "settings", "source_2.xml"));
            SidecarSettingsMigration.Run(android, data, NullLogger.Instance);
            Assert.False(File.Exists(Path.Combine(data, "settings", "source_2.xml")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    // The header Java's Properties.storeToXML writes, external DTD and all.
    static void Save(string file, Dictionary<string, string> values) =>
        new XDocument(
            new XDocumentType("properties", null, "http://java.sun.com/dtd/properties.dtd", null),
            new XElement("properties",
                values.Select(kv => new XElement("entry", new XAttribute("key", kv.Key), kv.Value)))).Save(file);

    static Dictionary<string, string> Load(string file) =>
        XDocument.Load(file).Root!.Elements("entry").ToDictionary(e => e.Attribute("key")!.Value, e => e.Value);
}
