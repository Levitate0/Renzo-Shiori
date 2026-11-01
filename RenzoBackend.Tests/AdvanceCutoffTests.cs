using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Downloads;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>The "continue after" point a download leaves behind (Philia, 2026-10-31).</summary>
public class AdvanceCutoffTests
{
    private static SeriesProviderEntity Provider(decimal? cutoff, params (decimal n, bool downloaded)[] chapters) => new()
    {
        ContinueAfterChapter = cutoff,
        Chapters = chapters.Select(c => new Chapter { Number = c.n, Filename = c.downloaded ? $"{c.n}.cbz" : null }).ToList(),
    };

    [Fact]
    public void Stops_below_chapters_still_waiting_to_download()
    {
        // 1-13 downloaded, 14-16 paid and not yet bought.
        var p = Provider(null, (12, true), (13, true), (14, false), (15, false), (16, false));
        Assert.Equal(13m, DownloadCommandService.AdvanceCutoff(p));
    }

    [Fact]
    public void A_download_past_a_gap_does_not_jump_the_gap()
    {
        var p = Provider(11, (12, true), (13, false), (26, true), (27, true));
        Assert.Equal(12m, DownloadCommandService.AdvanceCutoff(p));
    }

    [Fact]
    public void Never_moves_back_below_the_users_start_point()
    {
        // Added at chapter 50: 1-50 were never wanted.
        var p = Provider(50, (3, false), (49, false), (51, true));
        Assert.Equal(51m, DownloadCommandService.AdvanceCutoff(p));
    }

    [Fact]
    public void Nothing_waiting_means_the_highest_download()
    {
        var p = Provider(5, (6, true), (7, true));
        Assert.Equal(7m, DownloadCommandService.AdvanceCutoff(p));
    }
}
