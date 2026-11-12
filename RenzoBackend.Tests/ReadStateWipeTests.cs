using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Models;
using RenzoBackend.Models.ReadState;
using RenzoBackend.Services.ReadState;
using System.Text.Json;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// "All chapters went unread" (2026-11-11): a moment where renzo.json couldn't
/// be found got cached as "nothing read", and the next page turn wrote that
/// empty list over the real one.
/// </summary>
public class ReadStateWipeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "rs-" + Guid.NewGuid().ToString("N"));
    private readonly ReadStateCacheService _cache = new();
    private readonly ReadStateService _states;

    public ReadStateWipeTests()
    {
        Directory.CreateDirectory(_folder);
        var json = new RenzoJsonService(null!, NullLogger<RenzoJsonService>.Instance); // absolute paths never read settings
        _states = new ReadStateService(NullLogger<ReadStateService>.Instance, _cache, null!, json, new ReadStateChangeNotifier());
    }

    public void Dispose() => Directory.Delete(_folder, true);

    private string File => Path.Combine(_folder, "renzo.json");

    private void WriteRead(params decimal[] chapters) =>
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(new ImportSeriesSnapshot
        {
            Version = 2,
            UserReadStates = [new UserReadStateSnapshot
            {
                Username = "Levit",
                Chapters = chapters.Select(n => new ChapterReadState { ChapterNumber = n, IsCompleted = true, Progress = 1 }).ToList(),
            }],
        }));

    private List<decimal> OnDisk() =>
        JsonSerializer.Deserialize<ImportSeriesSnapshot>(System.IO.File.ReadAllText(File))!
            .UserReadStates!.Single(u => u.Username == "Levit").Chapters.Select(c => c.ChapterNumber).OrderBy(n => n).ToList();

    [Fact]
    public void A_missing_file_is_not_remembered_as_nothing_read()
    {
        Assert.Empty(_states.GetSeriesReadStates("Levit", _folder)); // renzo.json not there yet
        WriteRead(1, 2, 3);
        Assert.Equal(3, _states.GetSeriesReadStates("Levit", _folder).Count);
    }

    [Fact]
    public void Reading_a_chapter_with_a_stale_empty_cache_keeps_every_other_chapter()
    {
        WriteRead(1, 2, 3);
        _cache.SetCachedReadStates("Levit", _folder, []); // the old miss, as it used to be cached
        _states.SetReadState("Levit", Guid.Empty, Guid.Empty, "c4.cbz", null, "test", _folder, 4, 5, 10);
        Assert.Equal([1m, 2m, 3m, 4m], OnDisk());
        _states.SetCompleted("Levit", Guid.Empty, Guid.Empty, _folder, 5, true);
        _states.SetBookmark("Levit", _folder, 6, true);
        Assert.Equal([1m, 2m, 3m, 4m, 5m, 6m], OnDisk());
        Assert.Equal(6, _states.GetSeriesReadStates("Levit", _folder).Count);
    }

    [Fact]
    public void Nothing_is_written_where_the_series_folder_is_missing()
    {
        string gone = Path.Combine(_folder, "moved-away");
        _states.SetCompleted("Levit", Guid.Empty, Guid.Empty, gone, 1, true);
        Assert.False(Directory.Exists(gone));
    }
}
