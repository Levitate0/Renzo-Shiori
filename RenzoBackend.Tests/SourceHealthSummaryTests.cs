using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Downloads;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// What Sources › Health and the dead-source alert read off SourceHealthService
/// and SourceSpeedStats (2026-11-10).
/// </summary>
public class SourceHealthSummaryTests
{
    private DateTime _now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private SourceHealthService Health()
    {
        _now = _now.AddMinutes(-20);
        var health = new SourceHealthService(NullLogger<SourceHealthService>.Instance, () => _now);
        _now = _now.AddMinutes(20);
        return health;
    }

    private static SeriesProviderEntity Source(string name) => new()
    {
        Id = Guid.NewGuid(),
        Provider = name,
        MihonProviderId = name + "|1",
    };

    [Fact]
    public void Failures_are_counted_for_a_day_on_every_series_of_the_source()
    {
        var health = Health();
        SeriesProviderEntity a = Source("Anisa"), b = Source("Anisa");
        health.ReportFailure(a, "A", SourceFailureKind.Error, 1);
        health.ReportFailure(b, "B", SourceFailureKind.Timeout, 1);
        _now = _now.AddHours(20);
        health.ReportFailure(a, "A", SourceFailureKind.Error, 2);
        Assert.Equal(3, health.FailuresLastDay("Anisa"));

        _now = _now.AddHours(5);
        Assert.Equal(1, health.FailuresLastDay("Anisa"));
        Assert.Equal(0, health.FailuresLastDay("MangaFire"));
    }

    [Fact]
    public void A_stalled_queue_or_a_restarting_runner_is_not_a_failure()
    {
        var health = Health();
        SeriesProviderEntity a = Source("Comic Asura");
        health.ReportFailure(a, "A", SourceFailureKind.Backlog);
        health.ReportFailure(a, "A", SourceFailureKind.Transport);
        Assert.Equal(0, health.FailuresLastDay("Comic Asura"));
    }

    [Fact]
    public void A_success_is_remembered_and_a_step_down_says_why()
    {
        var health = Health();
        SeriesProviderEntity a = Source("AllManga");
        Assert.Null(health.LastSuccess("AllManga"));
        health.ReportFailure(a, "A", SourceFailureKind.Captcha);
        Assert.Equal("asks for a captcha", health.StepDownReason(a.Id, a.MihonProviderId));

        health.ReportSuccess(a, "A");
        Assert.Equal(_now, health.LastSuccess("AllManga"));
        Assert.Null(health.StepDownReason(a.Id, a.MihonProviderId));
    }

    [Fact]
    public void Last_delivered_is_the_newest_completed_download()
    {
        var speed = new SourceSpeedStats(NullLogger<SourceSpeedStats>.Instance);
        DateTime t = DateTime.UtcNow;
        speed.Record("MangaK", TimeSpan.FromSeconds(3), t.AddHours(-30));
        speed.Record("MangaK", null, t.AddHours(-1));
        Assert.Equal(t.AddHours(-30), speed.LastDelivered("MangaK"));
        Assert.Null(speed.LastDelivered("Nobody"));
    }
}
