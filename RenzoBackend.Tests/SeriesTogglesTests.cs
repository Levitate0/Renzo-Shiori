using RenzoBackend.Extensions;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// The series page reads its toggles back from the detail it loads. Prioritize
/// Free was missing from it (2026-11-08), so the button always showed off and
/// a click could only ever turn it on.
/// </summary>
public class SeriesTogglesTests
{
    [Fact]
    public void Series_detail_carries_the_per_series_toggles()
    {
        var s = new SeriesEntity
        {
            Title = "X", Sources = [], StoragePath = "x",
            HideDecimalChapters = true, PrioritizeFreeChapters = true, AutoBuyPaidChapters = false,
        };
        SeriesExtendedDto d = s.ToSeriesExtendedInfo(new SettingsDto());
        Assert.True(d.HideDecimalChapters);
        Assert.True(d.PrioritizeFreeChapters);
        Assert.False(d.AutoBuyPaidChapters);
    }
}
