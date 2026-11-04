using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Downloads;
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
}
