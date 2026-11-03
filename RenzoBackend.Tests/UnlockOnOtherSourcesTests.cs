using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Services.Downloads;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>A chapter owned on one source stops showing as paid on the others (2026-11-01).</summary>
public class UnlockOnOtherSourcesTests
{
    private static SeriesProviderEntity Provider(params (decimal n, bool locked)[] chapters) => new()
    {
        Id = Guid.NewGuid(),
        Chapters = chapters.Select(c => new Chapter
        {
            Number = c.n, IsLocked = c.locked, LockedCheckedAt = c.locked ? DateTime.UtcNow : null,
        }).ToList(),
    };

    [Fact]
    public void Clears_the_same_chapter_on_every_other_source_only()
    {
        var owner = Provider((20, true));
        var other = Provider((20, true), (21, true));
        var third = Provider((20, true));

        var changed = DownloadCommandService.UnlockOnOtherSources([owner, other, third], owner.Id, 20);

        Assert.Equal([other, third], changed);
        Assert.False(other.Chapters[0].IsLocked);
        Assert.Null(other.Chapters[0].LockedCheckedAt);
        Assert.True(other.Chapters[1].IsLocked);   // chapter 21 is not owned
        Assert.True(owner.Chapters[0].IsLocked);   // the owner's own row is the caller's job
        Assert.False(other.Chapters[0].ShouldDownload); // the owning source downloads it
    }

    [Fact]
    public void Nothing_locked_elsewhere_changes_nothing()
    {
        var owner = Provider((5, false));
        var other = Provider((5, false));
        Assert.Empty(DownloadCommandService.UnlockOnOtherSources([owner, other], owner.Id, 5));
    }

    [Theory]
    [InlineData("🔒 Chapter 3", "Chapter 3")]
    [InlineData("Chapter 6.1 🔐", "Chapter 6.1")]
    [InlineData("[Locked] Chapter 9", "Chapter 9")]
    [InlineData("Ch. 87 - Coin of Wishes (1)", "Ch. 87 - Coin of Wishes (1)")]
    public void Strips_only_the_lock_symbol_from_a_title(string name, string expected) =>
        Assert.Equal(expected, RenzoBackend.Extensions.ModelExtensions.StripLockSymbol(name));

    [Fact]
    public void A_sibling_with_only_a_lock_symbol_in_its_title_is_cleaned()
    {
        // Philia: IsLocked already clear, "🔒" still in the title, badge still "paid".
        var owner = Provider((3, false));
        var other = Provider((3, false));
        other.Chapters[0].Name = "🔒 Chapter 3";
        Assert.Equal([other], DownloadCommandService.UnlockOnOtherSources([owner, other], owner.Id, 3));
        Assert.Equal("Chapter 3", other.Chapters[0].Name);
    }
}
