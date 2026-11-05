using RenzoBackend.Services.Settings;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// PUT /api/settings/categories/order (2026-11-05) saves a new order only for exactly the
/// categories already saved, so a stale picker can't drop or add one.
/// </summary>
public class CategoryOrderTests
{
    private static readonly string[] Saved = ["Manga", "Manhwa", "Manhua"];

    [Fact]
    public void A_new_order_of_the_same_names_is_accepted() =>
        Assert.True(SettingsService.IsReorderOf(["Manhwa", "Manhua", "Manga"], Saved));

    [Fact]
    public void A_missing_or_extra_name_is_refused()
    {
        Assert.False(SettingsService.IsReorderOf(["Manhwa", "Manga"], Saved));
        Assert.False(SettingsService.IsReorderOf(["Manhwa", "Manga", "Manhua", "Comics"], Saved));
        Assert.False(SettingsService.IsReorderOf(["Manhwa", "Manga", "Comics"], Saved));
    }

    [Fact]
    public void A_repeated_name_or_another_case_is_refused()
    {
        Assert.False(SettingsService.IsReorderOf(["Manga", "Manga", "Manhua"], Saved));
        Assert.False(SettingsService.IsReorderOf(["manga", "Manhwa", "Manhua"], Saved));
    }
}
