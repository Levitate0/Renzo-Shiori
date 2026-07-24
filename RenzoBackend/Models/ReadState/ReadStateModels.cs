using RenzoBackend.Models;
using System.Text.Json;

namespace RenzoBackend.Models.ReadState;

/// <summary>
/// Per-user read state stored in renzo.json (never in the database).
/// </summary>
public class UserReadStateSnapshot
{
    public string Username { get; set; } = string.Empty;
    public List<ChapterReadState> Chapters { get; set; } = [];
}

public class ChapterReadState
{
    public bool IsCompleted { get; set; }
    public decimal ChapterNumber { get; set; }
    public float Progress { get; set; }
    public string? LastReadDeviceId { get; set; }
    public string? LastReadDeviceName { get; set; }
    public string? LastReadFilename { get; set; }
    public DateTime LastReadAt { get; set; }
    /// <summary>Chapter bookmark (built-in reader / Suwayomi import). Additive JSON field — absent in older files.</summary>
    public bool Bookmarked { get; set; }
}

/// <summary>
/// Hash cache for a series' chapters (stored in hashes directory, not renzo.json).
/// </summary>
public class SeriesHashCache
{
    public List<ChapterHashCache> Chapters { get; set; } = [];
}

public class ChapterHashCache
{
    public string ArchiveFilename { get; set; } = string.Empty;
    public DateTime ArchiveLastModifiedUtc { get; set; }
    /// <summary>
    /// Dictionary of page index -> Dictionary of mime type -> MD5 hex hash.
    /// </summary>
    public Dictionary<int, Dictionary<string,  string>> PageHashes { get; set; } = [];
}