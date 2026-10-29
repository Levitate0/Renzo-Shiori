using System.Text.RegularExpressions;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// The hide-18+ tag list lives in three places (web, server, Hub). On
/// 2026-08-21 one copy drifted and users got a filter 24 tags short. This fails
/// when the copies disagree. The Hub is a separate repo: it is compared only
/// when checked out next to shiori/, as on the server.
/// </summary>
public class AdultFilterParityTests
{
    [Fact]
    public void All_copies_list_the_same_tags()
    {
        string shiori = FindUp("RenzoFrontend");
        var web = Tags(Path.Combine(shiori, "RenzoFrontend/src/lib/utils/adult-filter.ts"), "const ADULT_TAGS = new Set([");
        var server = Tags(Path.Combine(shiori, "RenzoBackend/Services/Helpers/AdultContentClassifier.cs"), "AdultTags = new(StringComparer.OrdinalIgnoreCase)");

        Assert.True(web.Count >= 30, $"only {web.Count} tags parsed from the web copy");
        Assert.Equal(web, server);

        string hub = Path.Combine(shiori, "../hub/feature-shiori/src/jvmShared/kotlin/app/renzoshiori/client/ui/util/AdultFilter.kt");
        if (File.Exists(hub))
            Assert.Equal(web, Tags(hub, "private val ADULT_TAGS = setOf("));
    }

    /// <summary>Quoted strings from the marker to the end of the list, comments removed.</summary>
    static SortedSet<string> Tags(string file, string marker)
    {
        string text = File.ReadAllText(file);
        int start = text.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"list marker not found in {file}");
        var tags = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string line in text[(start + marker.Length)..].Split('\n'))
        {
            string code = Regex.Replace(line, "//.*$", "").Trim();
            if (code.StartsWith(']') || code.StartsWith(')') || code.StartsWith('}'))
                break;
            foreach (Match m in Regex.Matches(code, "\"([^\"]+)\""))
                tags.Add(m.Groups[1].Value);
        }
        return tags;
    }

    static string FindUp(string child)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, child)))
                return dir.FullName;
        throw new DirectoryNotFoundException(child);
    }
}
