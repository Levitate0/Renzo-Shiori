using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Search;
using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>Source migration (2026-11-04: MangaFire behind a Cloudflare block).</summary>
public class SourceMigrationTests
{
    private static SeriesProviderEntity Source(string name, string title, string? description, params decimal[] numbers) => new()
    {
        Id = Guid.NewGuid(),
        Provider = name,
        Title = title,
        Language = "en",
        Description = description,
        Chapters = numbers.Select(n => new Chapter { Number = n, Name = $"Chapter {n}", ShouldDownload = true }).ToList(),
    };

    [Fact]
    public void A_series_is_looked_for_under_every_name_it_has()
    {
        var s = new SeriesEntity
        {
            Title = "Solo Leveling",
            Sources =
            [
                Source("MangaFire", "Solo Leveling", "Alternative Names: Na Honjaman Level Up; 나 혼자만 레벨업"),
                Source("MangaK", "Solo Leveling (Official)", "Associated Name(s): I Level Up Alone"),
            ],
        };
        Assert.Equal(["Solo Leveling", "Solo Leveling (Official)", "Na Honjaman Level Up", "I Level Up Alone", "나 혼자만 레벨업"],
            SourceMigrationService.NamesOf(s));
    }

    [Fact]
    public void A_match_is_measured_against_the_old_sources_chapters_by_whole_number()
    {
        var mf = Source("MangaFire", "X", null, 1, 2, 2.5m, 3);
        mf.Chapters.Add(new Chapter { Number = 4, Name = "🔒 Chapter 4" });
        mf.Chapters.Add(new Chapter { Number = -1, Name = "Notice" });
        var s = new SeriesEntity { Title = "X", Sources = [mf, Source("Other", "X", null, 1, 2, 3, 4, 5, 6)] };
        Assert.Equal([1m, 2m, 3m], SourceMigrationService.WantedWholes(s, "MangaFire", null).OrderBy(n => n));
    }

    [Fact]
    public void Speeds_count_attempts_completions_and_the_median()
    {
        var stats = new SourceSpeedStats(NullLogger<SourceSpeedStats>.Instance);
        stats.Record("MangaK", TimeSpan.FromSeconds(2));
        stats.Record("MangaK", TimeSpan.FromSeconds(4));
        stats.Record("MangaK", TimeSpan.FromSeconds(3));
        stats.Record("MangaK", null);
        stats.Record("MangaFire", null, DateTime.UtcNow.AddDays(-8)); // older than the week
        var k = stats.For("MangaK");
        Assert.Equal((4, 3, 0.75, 3d), (k.Attempts, k.Downloads, k.SuccessRate!.Value, k.MedianSeconds!.Value));
        var f = stats.For("MangaFire");
        Assert.Equal(0, f.Attempts);
        Assert.Null(f.SuccessRate);
    }

    [Fact]
    public void Coverage_is_by_whole_number_and_nothing_wanted_proves_nothing()
    {
        Assert.Equal(1.0, SourceMigrationService.Coverage([1m, 2m, 3m], [1.1m, 2m, 3.5m, 4m]));
        Assert.Equal(0.5, SourceMigrationService.Coverage([1m, 2m], [2m, null]));
        Assert.Equal(0.0, SourceMigrationService.Coverage([], [1m, 2m]));
    }
    [Fact]
    public void The_latest_chapter_passes_over_a_stray_number()
    {
        Assert.Equal(63.5m, SourceMigrationService.Latest([1m, 2m, 61m, 62m, 63m, 63.5m, 2023m]));
        Assert.Equal(280m, SourceMigrationService.Latest([278m, 279m, 280m, 1000m]));
        Assert.Equal(1m, SourceMigrationService.Latest([1m]));
        Assert.Null(SourceMigrationService.Latest([]));
    }

    // ----- following a source to its new id: the accept rule -----

    /// <summary>The new id's source name, as the extension reports it.</summary>
    private const string Target = "Valir Scans";

    private static SeriesProviderEntity Row(string scanlator = "Valir Scans") => new()
    {
        Id = Guid.NewGuid(),
        Provider = "Valir Scans",
        Scanlator = scanlator,
        Title = "A Baby Cat Who Commands the Dog Clan",
        Language = "en",
    };

    private static ReplacementCandidate Group(string id, double coverage, string? scanlator = "Valir Scans", bool suspect = false) =>
        new(id, scanlator, coverage, suspect, new ProviderSeriesDetails { MihonId = id, Title = "A Baby Cat Who Commands the Dog Clan" });

    [Fact]
    public void One_exact_title_listing_the_rows_chapters_is_accepted()
    {
        ReplacementResult r = SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat"], [Group("cat", 1.0)], unreachable: false);
        Assert.Equal("matched", r.Status);
        Assert.Equal("cat", r.Chosen!.MihonId);
    }

    [Fact]
    public void Two_series_with_the_rows_title_are_never_picked_from()
    {
        ReplacementResult r = SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat", "cat2"], [Group("cat", 1.0)], unreachable: false);
        Assert.Equal(("ambiguous", null), (r.Status, r.Chosen));
    }

    [Fact]
    public void Coverage_below_nine_in_ten_is_not_enough()
    {
        Assert.Equal("low-coverage", SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat"], [Group("cat", 0.85)], false).Status);
        Assert.Equal("matched", SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat"], [Group("cat", 0.9)], false).Status);
    }

    [Fact]
    public void A_suspect_match_is_rejected()
    {
        Assert.Equal("no-match", SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat"], [Group("cat", 1.0, suspect: true)], false).Status);
    }

    [Fact]
    public void A_row_with_nothing_to_compare_is_never_matched()
    {
        Assert.Equal("low-coverage", SourceMigrationService.JudgeReplacement(Row(), Target, 0, ["cat"], [Group("cat", 1.0)], false).Status);
        Assert.Empty(SourceMigrationService.RowWholes(Row()));
    }

    [Fact]
    public void Another_scanlator_group_is_rejected()
    {
        // The row's scans keep only its own group, so they would find nothing.
        Assert.Equal("no-match", SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat"], [Group("cat", 1.0, "Other Group")], false).Status);
        // Of several groups, the row's own is the one measured.
        ReplacementResult r = SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat"],
            [Group("cat", 1.0, "Other Group"), Group("cat", 0.95, "Valir Scans")], false);
        Assert.Equal(("matched", 0.95), (r.Status, r.Coverage));
        Assert.Equal("matched", SourceMigrationService.JudgeReplacement(Row("Team X"), Target, 35, ["cat"], [Group("cat", 1.0, "Team X")], false).Status);
    }

    [Fact]
    public void The_scanlator_rule_is_the_scans_own()
    {
        Assert.True(SourceMigrationService.SameScanlator(Row(), "Valir Scans", Target));
        Assert.True(SourceMigrationService.SameScanlator(Row(""), "", Target));
        Assert.True(SourceMigrationService.SameScanlator(Row(""), "Valir Scans", Target));
        Assert.False(SourceMigrationService.SameScanlator(Row(), "Team X", Target));
        Assert.False(SourceMigrationService.SameScanlator(Row("Team X"), "Valir Scans", Target));
    }

    [Fact]
    public void A_recased_source_name_is_still_the_rows_group()
    {
        // A versionId bump can come with a new casing of the name; a chapter with
        // no group is listed under it, and the scan files it under the row's name.
        Assert.True(SourceMigrationService.SameScanlator(Row(), "Valir scans", "Valir scans"));
        Assert.True(SourceMigrationService.SameScanlator(Row(""), "VALIR SCANS", "Valir scans"));
        Assert.Equal("matched", SourceMigrationService.JudgeReplacement(Row(), "Valir scans", 35, ["cat"], [Group("cat", 1.0, "Valir scans")], false).Status);
        // Only the source's own name, and only for a row named after the source.
        Assert.False(SourceMigrationService.SameScanlator(Row(), "Valir scans", null));
        Assert.False(SourceMigrationService.SameScanlator(Row(), "Valir scans", "Valir Scans Team"));
        Assert.False(SourceMigrationService.SameScanlator(Row("Team X"), "Valir scans", "Valir scans"));
    }

    [Fact]
    public void A_site_that_could_not_be_searched_is_unreachable_not_a_miss()
    {
        Assert.Equal("unreachable", SourceMigrationService.JudgeReplacement(Row(), Target, 35, [], [], unreachable: true).Status);
        Assert.Equal("unreachable", SourceMigrationService.JudgeReplacement(Row(), Target, 35, ["cat"], [], unreachable: true).Status);
        Assert.Equal("no-match", SourceMigrationService.JudgeReplacement(Row(), Target, 35, [], [], unreachable: false).Status);
    }

    [Fact]
    public void Only_distinctive_other_names_are_searched()
    {
        var row = Source("Valir Scans", "The Father and the Daughter", "Alternative Names: Dear; Appa Ttal Iyagi");
        var s = new SeriesEntity { Title = "Like Father, Like Daughter", Sources = [row, Source("MangaHub", "Like Father, Like Daughter", null)] };
        Assert.Contains("Dear", AltTitles.Parse(row.Description));
        Assert.Equal(["The Father and the Daughter", "Like Father, Like Daughter", "Appa Ttal Iyagi"],
            SourceMigrationService.ReplacementNames(s, row));
    }
}
