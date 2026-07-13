using System.Text.Json.Serialization;

namespace RensaioBackend.Models.Dto;

public class ReaderChaptersDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    /// <summary>Series type (manga/manhwa/…): drives the default paged direction.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("chapters")]
    public List<ReaderChapterDto> Chapters { get; set; } = [];
}

public class ReaderChapterDto
{
    [JsonPropertyName("number")]
    public decimal Number { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Archive filename when downloaded; null = not on disk (not readable locally).</summary>
    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    [JsonPropertyName("pageCount")]
    public int? PageCount { get; set; }

    [JsonPropertyName("progress")]
    public float Progress { get; set; }

    [JsonPropertyName("isCompleted")]
    public bool IsCompleted { get; set; }

    [JsonPropertyName("bookmarked")]
    public bool Bookmarked { get; set; }

    [JsonPropertyName("lastReadAt")]
    public DateTime? LastReadAt { get; set; }
}

public class ReaderChapterInfoDto
{
    [JsonPropertyName("filename")]
    public string Filename { get; set; } = string.Empty;

    [JsonPropertyName("pageCount")]
    public int PageCount { get; set; }

    /// <summary>"webtoon" | "longstrip" | "paged" (see ReaderService heuristics).</summary>
    [JsonPropertyName("suggestedMode")]
    public string SuggestedMode { get; set; } = "paged";

    [JsonPropertyName("pages")]
    public List<ReaderPageDimsDto> Pages { get; set; } = [];
}

public class ReaderPageDimsDto
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("width")]
    public int? Width { get; set; }

    [JsonPropertyName("height")]
    public int? Height { get; set; }

    [JsonPropertyName("isStrip")]
    public bool IsStrip { get; set; }
}

public class ReaderProgressRequestDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("chapterNumber")]
    public decimal ChapterNumber { get; set; }

    [JsonPropertyName("filename")]
    public string? Filename { get; set; }

    /// <summary>1-based last page the user has seen.</summary>
    [JsonPropertyName("lastReadPage")]
    public int LastReadPage { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }
}

public class ReaderMarkRequestDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("chapterNumbers")]
    public List<decimal> ChapterNumbers { get; set; } = [];

    [JsonPropertyName("read")]
    public bool Read { get; set; }
}

public class ReaderBookmarkRequestDto
{
    [JsonPropertyName("seriesId")]
    public Guid SeriesId { get; set; }

    [JsonPropertyName("chapterNumber")]
    public decimal ChapterNumber { get; set; }

    [JsonPropertyName("bookmarked")]
    public bool Bookmarked { get; set; }
}

public class PreviewChapterDto
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("number")]
    public decimal? Number { get; set; }

    [JsonPropertyName("dateUpload")]
    public DateTime? DateUpload { get; set; }
}

public class PreviewChaptersDto
{
    [JsonPropertyName("mihonId")]
    public string MihonId { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("chapters")]
    public List<PreviewChapterDto> Chapters { get; set; } = [];
}

public class PreviewPagesDto
{
    [JsonPropertyName("pageCount")]
    public int PageCount { get; set; }
}

public class BackupImportResultDto
{
    [JsonPropertyName("backupSeries")]
    public int BackupSeries { get; set; }

    [JsonPropertyName("matchedSeries")]
    public int MatchedSeries { get; set; }

    [JsonPropertyName("updatedChapters")]
    public int UpdatedChapters { get; set; }

    [JsonPropertyName("bookmarks")]
    public int Bookmarks { get; set; }

    [JsonPropertyName("unmatched")]
    public List<string> Unmatched { get; set; } = [];
}
