using System.Text.Json.Nodes;
using RenzoBackend.Models.Database;

namespace RenzoBackend.Extensions;

/// <summary>
/// Priority-system preferences (default source order, the redownload-on-upgrade
/// toggle) are per-user, not instance-wide — each user's own library, own
/// sources, own opinion on which source wins. They live in the same
/// <see cref="UserEntity.Preferences"/> JSON blob the frontend already uses for
/// theme (see RenzoFrontend's theme-prefs.ts), under two extra top-level keys.
/// Reads/writes go through a loose <see cref="JsonNode"/> merge — NOT a
/// strongly-typed round-trip — so writing here never clobbers theme (or any
/// other future) keys already in the blob, and reading here never breaks on a
/// blob shaped for a different, unrelated set of keys.
/// </summary>
public static class UserPriorityPrefsExtensions
{
    private const string OrderKey = "defaultSourcePriorityOrder";
    private const string EnabledKey = "redownloadFromHigherPrioritySources";

    /// <summary>Global default source-priority order, by provider display name
    /// (lowest index = highest priority). Empty = not configured.</summary>
    public static string[] GetDefaultSourcePriorityOrder(this UserEntity user)
    {
        if (Parse(user.Preferences)?[OrderKey] is JsonArray arr)
            return arr.Select(n => (string?)n).Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToArray();
        return [];
    }

    /// <summary>Whether this user's series should re-download a chapter when a
    /// higher-priority source newly gains it. Temporary/manual today — flipped
    /// on by "Apply to All" once the user has a default order set up.</summary>
    public static bool GetRedownloadFromHigherPrioritySources(this UserEntity user)
        => Parse(user.Preferences)?[EnabledKey]?.GetValue<bool?>() ?? false;

    /// <summary>Merges the given field(s) into the user's preferences JSON and
    /// returns the new blob string to assign to <see cref="UserEntity.Preferences"/>
    /// — every other key already present (theme, onboarding, ...) is preserved
    /// untouched. Pass null for a field to leave it as-is.</summary>
    public static string SetPriorityPrefs(this UserEntity user, string[]? order = null, bool? enabled = null)
    {
        JsonObject obj = Parse(user.Preferences) as JsonObject ?? [];
        if (order != null)
            obj[OrderKey] = new JsonArray(order.Select(s => (JsonNode?)s).ToArray());
        if (enabled != null)
            obj[EnabledKey] = enabled.Value;
        return obj.ToJsonString();
    }

    private static JsonNode? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try { return JsonNode.Parse(json); }
        catch { return null; }
    }
}
