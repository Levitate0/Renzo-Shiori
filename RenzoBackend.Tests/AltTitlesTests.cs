using RenzoBackend.Services.Search;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>A series' other names, as source sites write them (2026-11-04).</summary>
public class AltTitlesTests
{
    [Fact]
    public void A_labelled_line_with_separators() =>
        Assert.Equal(["Na Honjaman Level Up", "I Level Up Alone", "나 혼자만 레벨업"],
            AltTitles.Parse("A weak hunter...\nAlternative Names: Na Honjaman Level Up; I Level Up Alone / 나 혼자만 레벨업\nStatus: Ongoing", "Solo Leveling"));

    [Fact]
    public void A_label_then_one_name_per_line() =>
        Assert.Equal(["The Villainess Is a Marionette", "Akuyaku Reijou wa Marionette"],
            AltTitles.Parse("Synopsis here.\n\nAlternative titles:\n• The Villainess Is a Marionette\n• Akuyaku Reijou wa Marionette\n\nRead more", "Villainess Marionette"));

    [Fact]
    public void Associated_names_with_commas()
    {
        Assert.Equal(["Tensura", "Slime Isekai"], AltTitles.Parse("Associated Name(s): Tensura, Slime Isekai"));
        // A single name with a comma in it, next to a stronger separator, stays whole.
        Assert.Equal(["Welcome Back, Papa", "Okaeri Papa"], AltTitles.Parse("Alt Titles: Welcome Back, Papa; Okaeri Papa"));
    }

    [Fact]
    public void A_synopsis_is_not_mistaken_for_names()
    {
        Assert.Empty(AltTitles.Parse("Alternatively, she could run away.\nOther kids laughed: loudly."));
        Assert.Empty(AltTitles.Parse(null));
        Assert.Empty(AltTitles.Parse("A long synopsis with no names at all."));
    }

    [Fact]
    public void The_series_own_title_and_repeats_are_left_out() =>
        Assert.Equal(["Kimetsu no Yaiba"], AltTitles.Parse("Alternative: Demon Slayer; Kimetsu no Yaiba; kimetsu no yaiba", "Demon Slayer"));

    [Fact]
    public void Keys_ignore_case_punctuation_and_ampersands()
    {
        Assert.Equal(AltTitles.Key("Spy x Family!"), AltTitles.Key("SPY x FAMILY"));
        Assert.Equal(AltTitles.Key("Love & Peace"), AltTitles.Key("Love and Peace"));
        Assert.NotEqual(AltTitles.Key("Manga X"), AltTitles.Key("Mang X"));
    }

    [Fact]
    public void Placeholders_and_repeated_labels_are_not_names()
    {
        Assert.Empty(AltTitles.Parse("Synopsis.\nAlternative Names: Unknown"));
        Assert.Empty(AltTitles.Parse("Associated Names: N/A"));
        Assert.Equal(["The Devil Raises a Lady"], AltTitles.Parse("Alternative Name: Alternative : The Devil Raises a Lady"));
    }

    [Fact]
    public void A_name_per_line_keeps_its_commas() =>
        Assert.Equal(["Goelobhyeojuseyo, ag-yeog-yeong-aenim!"],
            AltTitles.Parse("Alternative titles:\n- Please Bully Me, Miss Villainess!\n- Goelobhyeojuseyo, ag-yeog-yeong-aenim!\n- Aka no Grimoire", "Please Bully Me, Miss Villainess!").Take(1));

    [Fact]
    public void Pieces_of_the_own_title_are_not_names() =>
        Assert.Equal(["Nan Ran"], AltTitles.Parse("Alternative: One Day, Nan Ran", "One Day, the Male Lead Fell Into Our House"));

    [Fact]
    public void A_sentence_is_not_a_list_of_names() =>
        Assert.Empty(AltTitles.Parse("Also known as 深淵魔王 and others, the story follows Lin Tianhao."));

    [Fact]
    public void Generic_names_are_not_distinctive()
    {
        Assert.False(AltTitles.Distinctive("Unknown"));
        Assert.False(AltTitles.Distinctive("One Day"));
        Assert.True(AltTitles.Distinctive("Na Honjaman Level Up"));
    }
    [Fact]
    public void Markdown_bold_labels_are_labels()
    {
        // XCOMIC: the colon after the closing "**".
        Assert.Equal(["Gwinonggwichon Seonggonggi", "귀농귀촌 성공기"],
            AltTitles.Parse("Synopsis.\n\n**Alternative Titles**:\n- 귀농귀촌 성공기\n- Gwinonggwichon Seonggonggi\n\n**Status:** Ongoing"));
        // ReiManga / Kagane / Valir: the colon inside it.
        Assert.Equal(["Akuyaku Reijou wa Marionette"],
            AltTitles.Parse("**Associated Name(s):**\n• Akuyaku Reijou wa Marionette", "The Villainess Is a Marionette"));
    }

    [Fact]
    public void A_related_work_is_not_another_name()
    {
        // Kagane's spin-off lists the series it came from; a sequel adds a word.
        Assert.Equal(["Haitekku DxD"], AltTitles.Parse("Associated Name(s):\n• High School DxD\n• Haitekku DxD", "Junior High School DxD"));
        Assert.Empty(AltTitles.Parse("Alternative: Solo Leveling: Ragnarok", "Solo Leveling"));
        // A short leftover ("The") is still the series.
        Assert.Equal(["Villainess Turns the Hourglass"], AltTitles.Parse("Alternative: Villainess Turns the Hourglass", "The Villainess Turns the Hourglass"));
    }

    [Fact]
    public void Latin_names_come_first_and_tagged_scripts_do_not_count()
    {
        Assert.True(AltTitles.Latin("Aku menyukai Magical Girls"));
        Assert.False(AltTitles.Latin("とんがり帽子のアトリエ (Japanese)"));
        Assert.False(AltTitles.Latin("Средняя школа DxD"));
        Assert.False(AltTitles.Latin("Chōngjǐng chéngwéi mófǎ shàonǚ"));
        Assert.Equal(["Tongari Boushi no Atelier", "とんがり帽子のアトリエ (Japanese)"],
            AltTitles.Parse("Alternative: とんがり帽子のアトリエ (Japanese); Tongari Boushi no Atelier", "Witch Hat Atelier"));
    }

    [Fact]
    public void A_times_sign_is_an_x() =>
        Assert.Equal(AltTitles.Key("Spy x Family"), AltTitles.Key("SPY×FAMILY"));
}
