using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>The vcomics series id, as each build serialises it (Sana Scans, 2026-10).</summary>
public class VComicsPostIdTests
{
    [Fact]
    public void Reads_the_id_next_to_this_series_slug_even_with_an_apostrophe()
    {
        const string html =
            "data:$R[47]={slug:\"other-series\",post:$R[9]={id:12,slug:\"other-series\"}}," +
            "data:$R[48]={slug:\"chosen-as-the-dragon-crown-prince's-mate\",post:$R[49]={id:576,slug:\"chosen-as-the-dragon-crown-prince's-mate\"}}";
        Assert.Equal("576", LockedChapterSupplementService.FindPostId(html,
            "https://sanascans.com/series/chosen-as-the-dragon-crown-prince's-mate"));
    }

    [Fact]
    public void Reads_the_legacy_astro_form()
    {
        Assert.Equal("928", LockedChapterSupplementService.FindPostId(
            "{&quot;postId&quot;:[0,928]}", "https://magustoon.org/series/x"));
    }
}
