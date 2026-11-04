using RenzoBackend.Extensions;
using RenzoBackend.Models.Abstractions;
using RenzoBackend.Models.Dto;
using Mihon.ExtensionsBridge.Models.Extensions;
using System.Text.Json.Serialization;

namespace RenzoBackend.Models;

public class ChapterDownload : DownloadSummaryBase, IBridgeItemInfo
{
    public Guid Id { get; set; }
    public Guid SeriesId { get; set; }
    public Guid SeriesProviderId { get; set; }
    public string BridgeItemInfo { get; set; }
    public string MihonId { get; set; }
    public string MihonProviderId { get; set; }
    public int Index { get; set; }
    public int PageCount { get; set; }
    public long? SourceId { get; set; }
    public string? MangaUrl { get; set; }
    public string? ChapterUrl { get; set; }
    [JsonIgnore]
    public string ProviderName
    {
        get => Provider;
        set => Provider = value;
    }
    public string ChapterName { get; set; } = string.Empty;
    public string SeriesTitle { get; set; } = "";

    public int Retries { get; set; }

    /// <summary>
    /// Retries after our own plumbing failed (the extension runner restarted
    /// mid-chapter). They cost nothing from <see cref="Retries"/>, but only up to
    /// a cap: a failure that only LOOKS like a restart and happens every time
    /// would otherwise be retried every few minutes for ever.
    /// </summary>
    public int TransientRetries { get; set; }

    /// <summary>
    /// The series' sources this chapter has already failed on, carried from one
    /// hand-off to the next. A hand-off never goes back to one of them, so a
    /// chapter broken on two sources cannot bounce between them for ever, and
    /// once every source is in here the chapter is given up on.
    /// </summary>
    public List<Guid> TriedSourceIds { get; set; } = [];

    /// <summary>
    /// Sources the chapter left early, after one failure stepped the source down
    /// (its retries there unused). An early hand-off never goes back to one; at
    /// the end of the line, before giving up, one may be tried again, ranked last.
    /// </summary>
    public List<Guid> LeftEarlySourceIds { get; set; } = [];

    /// <summary>
    /// This job was handed on from another source (a step-down or a move off a
    /// stalled queue). Finding the chapter paid here then hands it on again,
    /// instead of ending the chain with the chapter queued nowhere.
    /// </summary>
    public bool HandedOff { get; set; }

    /// <summary>
    /// The user asked for this chapter from this source ("Re-download from X").
    /// It is tried there even while the source is stepped down, and keeps its
    /// full retry budget there before it is handed on.
    /// </summary>
    public bool ForcedSource { get; set; }


    private string _storagePath = string.Empty;
    public string StoragePath
    {
        get => _storagePath.SanitizeDirectory();
        set => _storagePath=value;
    }
    public ParsedChapter Chapter { get; set; } = new ParsedChapter();
    public List<string> Tags { get; set; } = [];
    public long? ChapterCount { get; set; }
    public string? Author { get; set; }
    public string? Artist { get; set; }
    public DateTime? ComicUploadDateUTC { get; set; }
    public string? Type { get; set; }

    public List<Page> Pages { get; set; }= [];
    public bool IsUpdate { get; set; } = false;
}