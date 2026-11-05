using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Mihon.ExtensionsBridge.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Downloads;

namespace RenzoBackend.Services.Providers;

/// <summary>One source an extension update gave a new id (see <see cref="SourceIdChanges"/>).</summary>
public sealed record SourceIdChange(string Package, string OldSourceId, string NewSourceId, string Name, string Language)
{
    /// <summary>"package|oldId", as Providers, SeriesProviders and UserProviders store it.</summary>
    public string OldProviderId => Package + "|" + OldSourceId;
    public string NewProviderId => Package + "|" + NewSourceId;
}

/// <summary>
/// Sources an extension update moved to a new id.
///
/// A source's id is a hash of its name, language and a versionId the extension
/// author bumps when a site changes so much that stored links stop working
/// (HttpSource.generateId). Valir Scans went 3→4 at 1.6.23 (2026-08-28, a new
/// site theme) and Asmodeus Scans 1→2 at 1.6.27 (2026-10-15, new URL scheme).
/// The old id then left the extension: its Providers row went dead, every library
/// row on it was marked uninstalled and stopped updating, and two series that had
/// it as their only source got nothing for weeks. The host-move fixer never saw
/// it, since it follows one id to a new host, and Migrate refuses a target with
/// the same name.
///
/// A change is recognised only on the extension's own evidence, never on the
/// IsDead or IsUninstalled flags (a reinstall clears those): the old id is gone
/// from the installed version and from every repository, the installed version
/// has exactly one source of the same name and language, that source is usable,
/// and the change runs forward (no version listing the old id is newer, and no
/// version lists both, which would make them two sources). The versionId pair is
/// worked out for the log only: extensions report it unreliably.
/// </summary>
public static class SourceIdChanges
{
    public const string Matched = "matched";
    public const string Ambiguous = "ambiguous";
    public const string NoMatch = "no-match";
    public const string LowCoverage = "low-coverage";
    public const string Unreachable = "unreachable";
    public const string Superseded = "superseded";
    public const string Manual = "manual";

    /// <summary>Judged-and-failed results (no-match, ambiguous, low-coverage) tried this often before they are left to the owner.</summary>
    internal const int MaxTries = 3;

    /// <summary>Between those tries: the catalogue grows, a renamed series may turn up.</summary>
    internal static readonly TimeSpan RetryInterval = TimeSpan.FromDays(7);

    /// <param name="providers">Every Providers row, dead ones included.</param>
    /// <param name="local">The installed extensions with their version history (MihonBridgeService.ListExtensions).</param>
    /// <param name="online">The repositories' indexes (MihonBridgeService.ListOnlineRepositories).</param>
    public static List<SourceIdChange> Detect(IEnumerable<ProviderStorageEntity> providers, IEnumerable<RepositoryGroup> local,
        IEnumerable<TachiyomiRepository> online)
    {
        List<ProviderStorageEntity> rows = providers.Where(p => !string.IsNullOrEmpty(p.MihonProviderId)).ToList();
        Dictionary<string, ProviderStorageEntity> byId = rows
            .GroupBy(p => p.MihonProviderId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        List<RepositoryGroup> groups = local.ToList();
        List<TachiyomiExtension> offered = online.SelectMany(r => r.Extensions ?? []).ToList();

        var changes = new List<SourceIdChange>();
        foreach (ProviderStorageEntity p in rows)
        {
            if (!Split(p.MihonProviderId, out string package, out string oldId))
                continue;
            RepositoryGroup? group = groups.FirstOrDefault(g => Active(g)?.Extension?.Package == package);
            RepositoryEntry? active = group == null ? null : Active(group);
            // The whole package is gone: there is nothing to follow to.
            if (group == null || active?.Extension == null)
                continue;
            // Still current, or a repository still offers it: a pinned or rolled
            // back version can bring it back, so it is not replaced.
            if (Lists(active, oldId))
                continue;
            if (offered.Any(e => e.Package == package && (e.Sources ?? []).Any(s => s.Id == oldId)))
                continue;
            // Exactly one source of the same name and language: two would be a guess.
            List<TachiyomiSource> same = (active.Extension.Sources ?? [])
                .Where(s => string.Equals(s.Name, p.Name, StringComparison.OrdinalIgnoreCase) && s.Language == p.Language)
                .ToList();
            if (same.Count != 1)
                continue;
            string newId = same[0].Id;
            if (!byId.TryGetValue(package + "|" + newId, out ProviderStorageEntity? next) || !next.IsActive)
                continue;
            // Forward only, so a rollback never ping-pongs rows between the two.
            int code = active.Extension.VersionCode;
            List<RepositoryEntry> history = group.Entries.Where(e => Lists(e, oldId)).ToList();
            if (history.Any(e => e.Extension.VersionCode >= code))
                continue;
            // Both ids in one version are two sources living side by side, not a replacement.
            if (history.Any(e => Lists(e, newId)))
                continue;
            changes.Add(new SourceIdChange(package, oldId, newId, same[0].Name, same[0].Language));
        }
        return changes;
    }

    /// <summary>
    /// HttpSource.generateId: the first 8 bytes of MD5("name.lowercase()/lang/versionId"),
    /// big-endian, sign bit cleared.
    /// </summary>
    public static long GenerateId(string name, string lang, int versionId)
    {
        byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes($"{name.ToLowerInvariant()}/{lang}/{versionId}"));
        long id = 0;
        for (int i = 0; i < 8; i++)
            id = (id << 8) | hash[i];
        return id & long.MaxValue;
    }

    /// <summary>The versionId that gives this source <paramref name="id"/>, when it is a small one; null otherwise. Evidence for the log only.</summary>
    public static int? VersionOf(string name, string lang, string id)
    {
        if (!long.TryParse(id, out long want))
            return null;
        for (int v = 0; v < 100; v++)
        {
            if (GenerateId(name, lang, v) == want)
                return v;
        }
        return null;
    }

    /// <summary>A library row's last result, kept only while it failed (source-id-follow.local.json, by row id).</summary>
    public sealed class FollowState
    {
        [JsonPropertyName("from")] public string From { get; set; } = "";
        [JsonPropertyName("to")] public string To { get; set; } = "";
        [JsonPropertyName("status")] public string Status { get; set; } = "";
        [JsonPropertyName("attempts")] public int Attempts { get; set; }
        [JsonPropertyName("nextTry")] public DateTime NextTry { get; set; }
    }

    /// <summary>Whether a row is looked at this pass. A result recorded for another new id (a later change) does not count.</summary>
    public static bool Due(FollowState? state, SourceIdChange change, DateTime now) =>
        state == null || state.To != change.NewProviderId || (state.Status != Manual && now >= state.NextTry);

    /// <summary>
    /// The state after a failed try. Unreachable backs off like a chapter that
    /// ran out of retries (6 h, 12 h … a week); a judged failure is tried again a
    /// week later and, after <see cref="MaxTries"/>, left to the owner (manual).
    /// </summary>
    public static FollowState Next(FollowState? state, SourceIdChange change, string status, DateTime now)
    {
        FollowState? prev = state?.To == change.NewProviderId ? state : null;
        var next = new FollowState { From = change.OldProviderId, To = change.NewProviderId, Status = status, Attempts = 1 };
        if (status == Unreachable)
        {
            if (prev?.Status == Unreachable)
                next.Attempts = prev.Attempts + 1;
            next.NextTry = now + DownloadsExtensions.GiveUpBackoff(next.Attempts);
            return next;
        }
        if (status is NoMatch or Ambiguous or LowCoverage)
        {
            if (prev?.Status is NoMatch or Ambiguous or LowCoverage)
                next.Attempts = prev.Attempts + 1;
            if (next.Attempts >= MaxTries)
                next.Status = Manual;
        }
        next.NextTry = now + RetryInterval;
        return next;
    }

    private static RepositoryEntry? Active(RepositoryGroup g) =>
        g.Entries != null && g.ActiveEntry >= 0 && g.ActiveEntry < g.Entries.Count ? g.Entries[g.ActiveEntry] : null;

    private static bool Lists(RepositoryEntry e, string sourceId) =>
        e.Extension?.Sources?.Any(s => s.Id == sourceId) == true;

    private static bool Split(string providerId, out string package, out string sourceId)
    {
        int bar = providerId.LastIndexOf('|');
        package = bar > 0 ? providerId[..bar] : "";
        sourceId = bar > 0 ? providerId[(bar + 1)..] : "";
        return package.Length > 0 && sourceId.Length > 0;
    }
}
