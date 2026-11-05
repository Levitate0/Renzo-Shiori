using Mihon.ExtensionsBridge.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Providers;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// Recognising a source an extension update gave a new id (Valir Scans 3→4 on
/// 2026-08-28, Asmodeus Scans 1→2 on 2026-10-15: seven library rows silently
/// stopped updating).
/// </summary>
public class SourceIdChangesTests
{
    private const string Pkg = "eu.kanade.tachiyomi.extension.en.asmotoon";
    private const string OldId = "5258838449396844455";
    private const string NewId = "2076371496779298312";

    private static TachiyomiSource Src(string id, string name = "Asmodeus Scans", string lang = "en") =>
        new() { Id = id, Name = name, Language = lang };

    private static RepositoryEntry Entry(int code, params TachiyomiSource[] sources) => new()
    {
        Extension = new TachiyomiExtension { Package = Pkg, VersionCode = code, Sources = sources.ToList() },
    };

    private static RepositoryGroup Group(int active, params RepositoryEntry[] entries) =>
        new() { Name = "tachiyomi-en.asmotoon", ActiveEntry = active, Entries = entries.ToList() };

    private static TachiyomiRepository Online(params TachiyomiSource[] sources)
    {
        var repo = new TachiyomiRepository("https://repo") { Id = "64e78107" };
        repo.Extensions.Add(new TachiyomiExtension { Package = Pkg, VersionCode = 106028, Sources = sources.ToList() });
        return repo;
    }

    private static ProviderStorageEntity Row(string id, bool enabled = true, bool dead = false, string name = "Asmodeus Scans", string lang = "en") => new()
    {
        MihonProviderId = Pkg + "|" + id,
        Name = name,
        Language = lang,
        SourcePackageName = Pkg,
        SourceSourceId = id,
        IsEnabled = enabled,
        IsDead = dead,
    };

    /// <summary>What the live install had: 1.6.25 lists the old id, 1.6.27 and 1.6.28 the new one.</summary>
    private static RepositoryGroup Asmodeus() =>
        Group(2, Entry(106025, Src(OldId)), Entry(106027, Src(NewId)), Entry(106028, Src(NewId)));

    private static List<ProviderStorageEntity> Rows() => [Row(OldId, enabled: false, dead: true), Row(NewId)];

    [Fact]
    public void The_ids_are_the_extensions_own_hash_of_name_language_and_version()
    {
        Assert.Equal(5258838449396844455, SourceIdChanges.GenerateId("Asmodeus Scans", "en", 1));
        Assert.Equal(2076371496779298312, SourceIdChanges.GenerateId("Asmodeus Scans", "en", 2));
        Assert.Equal(8440187396173658748, SourceIdChanges.GenerateId("Valir Scans", "en", 3));
        Assert.Equal(9114028255622294379, SourceIdChanges.GenerateId("Valir Scans", "en", 4));
        Assert.Equal(1, SourceIdChanges.VersionOf("Asmodeus Scans", "en", OldId));
        Assert.Equal(2, SourceIdChanges.VersionOf("Asmodeus Scans", "en", NewId));
        Assert.Null(SourceIdChanges.VersionOf("Asmodeus Scans", "en", "12345"));
    }

    [Fact]
    public void One_same_name_same_language_replacement_is_a_change()
    {
        SourceIdChange change = Assert.Single(SourceIdChanges.Detect(Rows(), [Asmodeus()], [Online(Src(NewId))]));
        Assert.Equal(Pkg + "|" + OldId, change.OldProviderId);
        Assert.Equal(Pkg + "|" + NewId, change.NewProviderId);
        Assert.Equal(("Asmodeus Scans", "en"), (change.Name, change.Language));
    }

    [Fact]
    public void Recognised_by_the_repositories_not_by_the_dead_flag()
    {
        // A reinstall revives the old row's flags; the evidence still holds.
        List<ProviderStorageEntity> rows = [Row(OldId), Row(NewId)];
        Assert.Single(SourceIdChanges.Detect(rows, [Asmodeus()], [Online(Src(NewId))]));
    }

    [Fact]
    public void A_pruned_history_still_counts()
    {
        RepositoryGroup pruned = Group(0, Entry(106028, Src(NewId)));
        Assert.Single(SourceIdChanges.Detect(Rows(), [pruned], []));
    }

    [Fact]
    public void Two_same_name_sources_are_a_guess()
    {
        RepositoryGroup two = Group(1, Entry(106025, Src(OldId)), Entry(106028, Src(NewId), Src("111")));
        List<ProviderStorageEntity> rows = [.. Rows(), Row("111")];
        Assert.Empty(SourceIdChanges.Detect(rows, [two], []));
    }

    [Fact]
    public void An_old_id_a_repository_still_offers_is_not_replaced()
    {
        // A pinned rollback can bring it back.
        Assert.Empty(SourceIdChanges.Detect(Rows(), [Asmodeus()], [Online(Src(OldId))]));
    }

    [Fact]
    public void A_package_that_is_gone_has_nothing_to_follow_to()
    {
        Assert.Empty(SourceIdChanges.Detect(Rows(), [], [Online(Src(NewId))]));
    }

    [Fact]
    public void A_switched_off_extension_that_still_lists_its_id_is_no_change()
    {
        // Galaxy Manga: the user disabled it; the id is still in the installed version.
        RepositoryGroup galaxy = Group(0, Entry(106000, Src(OldId)));
        Assert.Empty(SourceIdChanges.Detect([Row(OldId, enabled: false)], [galaxy], [Online(Src(OldId))]));
    }

    [Fact]
    public void A_renamed_or_relanguaged_source_is_not_followed()
    {
        RepositoryGroup renamed = Group(1, Entry(106025, Src(OldId)), Entry(106028, Src(NewId, name: "Asmodeus Comics")));
        Assert.Empty(SourceIdChanges.Detect([Row(OldId), Row(NewId, name: "Asmodeus Comics")], [renamed], []));

        RepositoryGroup otherLang = Group(1, Entry(106025, Src(OldId)), Entry(106028, Src(NewId, lang: "es")));
        Assert.Empty(SourceIdChanges.Detect([Row(OldId), Row(NewId, lang: "es")], [otherLang], []));
    }

    [Fact]
    public void The_name_is_compared_ignoring_case()
    {
        RepositoryGroup recased = Group(1, Entry(106025, Src(OldId)), Entry(106028, Src(NewId, name: "ASMODEUS SCANS")));
        Assert.Single(SourceIdChanges.Detect([Row(OldId), Row(NewId, name: "ASMODEUS SCANS")], [recased], []));
    }

    [Fact]
    public void Only_forward_a_newer_version_with_the_old_id_is_a_rollback()
    {
        // The installed version (code 106027) is older than one listing the old id.
        RepositoryGroup rolledBack = Group(0, Entry(106027, Src(NewId)), Entry(106028, Src(OldId)));
        Assert.Empty(SourceIdChanges.Detect(Rows(), [rolledBack], []));
    }

    [Fact]
    public void Both_ids_in_one_version_are_two_sources()
    {
        RepositoryGroup both = Group(1, Entry(106025, Src(OldId), Src(NewId)), Entry(106028, Src(NewId)));
        Assert.Empty(SourceIdChanges.Detect(Rows(), [both], []));
    }

    [Fact]
    public void The_new_id_must_be_usable()
    {
        Assert.Empty(SourceIdChanges.Detect([Row(OldId, dead: true), Row(NewId, enabled: false)], [Asmodeus()], []));
        Assert.Empty(SourceIdChanges.Detect([Row(OldId, dead: true)], [Asmodeus()], []));
    }

    // ----- the loop guard -----

    private static readonly SourceIdChange Change = new(Pkg, OldId, NewId, "Asmodeus Scans", "en");
    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Unreachable_backs_off_like_a_given_up_chapter()
    {
        var first = SourceIdChanges.Next(null, Change, SourceIdChanges.Unreachable, Now);
        Assert.Equal((SourceIdChanges.Unreachable, 1, Now.AddHours(6)), (first.Status, first.Attempts, first.NextTry));
        Assert.False(SourceIdChanges.Due(first, Change, Now.AddHours(5)));
        Assert.True(SourceIdChanges.Due(first, Change, Now.AddHours(6)));

        var second = SourceIdChanges.Next(first, Change, SourceIdChanges.Unreachable, Now.AddHours(6));
        Assert.Equal((2, Now.AddHours(18)), (second.Attempts, second.NextTry));
    }

    [Fact]
    public void Three_judged_failures_leave_it_to_the_owner_for_good()
    {
        SourceIdChanges.FollowState? s = null;
        DateTime t = Now;
        foreach (string status in new[] { SourceIdChanges.NoMatch, SourceIdChanges.LowCoverage })
        {
            s = SourceIdChanges.Next(s, Change, status, t);
            Assert.Equal(status, s.Status);
            Assert.False(SourceIdChanges.Due(s, Change, t.AddDays(6)));
            t = t.AddDays(7);
            Assert.True(SourceIdChanges.Due(s, Change, t));
        }
        s = SourceIdChanges.Next(s, Change, SourceIdChanges.Ambiguous, t);
        Assert.Equal((SourceIdChanges.Manual, 3), (s.Status, s.Attempts));
        Assert.False(SourceIdChanges.Due(s, Change, t.AddYears(1)));
    }

    [Fact]
    public void A_later_change_of_the_same_row_starts_over()
    {
        var manual = new SourceIdChanges.FollowState { From = Change.OldProviderId, To = Pkg + "|999", Status = SourceIdChanges.Manual, Attempts = 3 };
        Assert.True(SourceIdChanges.Due(manual, Change, Now));
        Assert.Equal(1, SourceIdChanges.Next(manual, Change, SourceIdChanges.NoMatch, Now).Attempts);
    }

    [Fact]
    public void Superseded_is_looked_at_again_a_week_later()
    {
        var s = SourceIdChanges.Next(null, Change, SourceIdChanges.Superseded, Now);
        Assert.Equal((SourceIdChanges.Superseded, Now.AddDays(7)), (s.Status, s.NextTry));
    }
}
