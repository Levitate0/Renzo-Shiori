using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using RenzoBackend.Controllers;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Models.Enums;
using RenzoBackend.Models.ReadState;
using RenzoBackend.Services.ReadState;
using RenzoBackend.Services.Reader;
using RenzoBackend.Services.Series;
using RenzoBackend.Services.SiteAuth;
using RenzoBackend.Services.Users;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-15 audit, residual pass: username twins of unowned library folders
/// (H6a), the shared site-login jar (L18), and the backup import's CPU and
/// write amplifiers (M11).
/// </summary>
public class ResidualFindingsTests : IDisposable
{
    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rf-" + Guid.NewGuid().ToString("N"));

    public ResidualFindingsTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        Db().Database.EnsureCreated();
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _contexts.ForEach(c => c.Dispose());
        _conn.Dispose();
        Directory.Delete(_root, true);
    }

    private AppDbContext Db()
    {
        var db = new AppDbContext(_options);
        _contexts.Add(db);
        return db;
    }

    private UserEntity AddUser(UserLevel level, string? name = null, DateTime? createdAt = null)
    {
        var u = new UserEntity
        {
            Id = Guid.NewGuid(), Username = name ?? $"{level}-{Guid.NewGuid():N}"[..20], Level = level,
            OpdsPath = Guid.NewGuid().ToString("N"), IsActive = true, CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        var db = Db();
        db.Users.Add(u);
        db.SaveChanges();
        return u;
    }

    private string OldFile(string relativeDir, DateTime writtenUtc)
    {
        string dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        string f = Path.Combine(dir, "Chapter 1.cbz");
        File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(f, writtenUtc);
        Directory.SetLastWriteTimeUtc(dir, writtenUtc);   // as the folder would be on a real library
        return dir;
    }

    // ---- H6(a): a username may not twin an unowned library folder ----

    [Theory]
    [InlineData("manga")]
    [InlineData("MANGA")]
    [InlineData("Manga")]
    public void A_username_matching_a_library_folder_with_files_is_refused(string name)
    {
        OldFile("Manga/Some Title", DateTime.UtcNow.AddDays(-30));
        Assert.NotNull(UserCommandService.LibraryFolderTwinProblem(_root, name));
    }

    [Fact]
    public void A_username_is_fine_next_to_an_empty_folder_or_none()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Empty"));
        Assert.Null(UserCommandService.LibraryFolderTwinProblem(_root, "empty"));
        Assert.Null(UserCommandService.LibraryFolderTwinProblem(_root, "newcomer"));
        Assert.Null(UserCommandService.LibraryFolderTwinProblem(Path.Combine(_root, "missing"), "manga"));
    }

    [Fact]
    public async Task A_new_series_in_a_folder_older_than_the_account_is_refused()
    {
        OldFile("manga/Old Title", DateTime.UtcNow.AddDays(-30));
        UserEntity manager = AddUser(UserLevel.Manager, "manga");
        var settings = new SettingsDto { StorageFolder = _root };

        Assert.True(await SeriesCommandService.FolderPredatesOwnerAsync(Db(), settings, "manga/Old Title", manager.Id, default));
        // The user's root itself (a series pointed at the whole folder) too.
        Assert.True(await SeriesCommandService.FolderPredatesOwnerAsync(Db(), settings, "manga", manager.Id, default));
    }

    [Fact]
    public async Task The_normal_add_flow_and_a_re_add_of_ones_own_files_still_work()
    {
        UserEntity manager = AddUser(UserLevel.Manager, "reader1", DateTime.UtcNow.AddDays(-10));
        var settings = new SettingsDto { StorageFolder = _root };

        // A brand-new folder (the web wizard / Hub send {StorageFolder}/Title, prefixed to reader1/Title).
        Assert.False(await SeriesCommandService.FolderPredatesOwnerAsync(Db(), settings, "reader1/New Title", manager.Id, default));
        // A series they deleted while keeping its files: written after the account existed.
        OldFile("reader1/Kept Title", DateTime.UtcNow.AddDays(-2));
        Assert.False(await SeriesCommandService.FolderPredatesOwnerAsync(Db(), settings, "reader1/Kept Title", manager.Id, default));
    }

    [Fact]
    public async Task The_owner_is_not_limited_by_folder_age()
    {
        OldFile("Owner/Old", DateTime.UtcNow.AddDays(-300));
        UserEntity owner = AddUser(UserLevel.Owner, "Owner");
        Assert.False(await SeriesCommandService.FolderPredatesOwnerAsync(Db(), new SettingsDto { StorageFolder = _root }, "Owner/Old", owner.Id, default));
    }

    // ---- L18: only the Owner's site login touches the shared jar ----

    private static SiteAuthService SiteAuth(AppDbContext db) =>
        // Jar, protector, registry etc. are null on purpose: a refused caller must
        // never reach them.
        new(db, null!, null!, null!, null!, null!, null!, NullLogger<SiteAuthService>.Instance);

    [Theory]
    [InlineData(UserLevel.User)]
    [InlineData(UserLevel.Manager)]
    [InlineData(UserLevel.Admin)]
    public async Task Below_owner_a_site_login_never_reaches_the_jar(UserLevel level)
    {
        UserEntity u = AddUser(level);
        var (cred, result) = await SiteAuth(Db()).SaveAndLoginAsync(u.Id, "SiteA", "me", "pw");
        Assert.False(result.Success);
        Assert.Equal(Guid.Empty, cred.Id);
        Assert.False((await SiteAuth(Db()).SaveCookieAsync(u.Id, "SiteA", "me", "session=abc")).result.Success);
        Assert.Empty(Db().SiteCredentials.Where(c => c.UserId == u.Id));
    }

    [Fact]
    public async Task A_lower_accounts_old_row_is_neither_used_nor_able_to_clear_the_jar()
    {
        UserEntity admin = AddUser(UserLevel.Admin);
        var db = Db();
        var row = new SiteCredentialEntity { Id = Guid.NewGuid(), UserId = admin.Id, Provider = "SiteA", Username = "me" };
        db.SiteCredentials.Add(row);
        db.SaveChanges();

        Assert.False((await SiteAuth(Db()).LoginAsync(row)).Success);
        await SiteAuth(Db()).DeleteAsync(admin.Id, row.Id);   // null jar: would throw if it tried to clear it
        Assert.Empty(Db().SiteCredentials.Where(c => c.Id == row.Id));
    }

    // ---- M11: backup import amplifiers ----

    private static int ReferenceLevenshtein(string a, string b)
    {
        var m = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) m[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) m[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
            for (int j = 1; j <= b.Length; j++)
                m[i, j] = Math.Min(Math.Min(m[i - 1, j] + 1, m[i, j - 1] + 1), m[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
        return m[a.Length, b.Length];
    }

    [Fact]
    public void Two_row_levenshtein_matches_the_full_matrix()
    {
        var rng = new Random(1234);
        string Rand(int max) => new(Enumerable.Range(0, rng.Next(max)).Select(_ => "abcde ü"[rng.Next(7)]).ToArray());
        for (int n = 0; n < 2000; n++)
        {
            string a = Rand(25), b = Rand(25);
            Assert.Equal(ReferenceLevenshtein(a, b), a.LevenshteinDistance(b));
            Assert.Equal(ReferenceLevenshtein(a, b), b.LevenshteinDistance(a));
        }
        Assert.Equal(3, "kitten".LevenshteinDistance("sitting"));
        Assert.Equal(5, "".LevenshteinDistance("hello"));
    }

    [Fact]
    public void Similarity_answers_are_unchanged_by_the_length_shortcut()
    {
        var rng = new Random(99);
        string Rand(int max) => new(Enumerable.Range(0, rng.Next(1, max)).Select(_ => "abcxyz"[rng.Next(6)]).ToArray());
        for (int n = 0; n < 2000; n++)
        {
            string a = Rand(30), b = Rand(30);
            // The old answer: no shortcut, full distance over the normalised titles.
            string na = StringExtensions.NormalizeTitle(a), nb = StringExtensions.NormalizeTitle(b);
            int max = Math.Max(na.Length, nb.Length);
            bool expected = max > 0 && (double)ReferenceLevenshtein(na, nb) / max <= 0.1;
            Assert.Equal(expected, a.AreStringSimilar(b));
        }
        Assert.True("Solo Leveling".AreStringSimilar("Solo Levelling"));
    }

    private static byte[] Varint(int v)
    {
        var bytes = new List<byte>();
        uint u = (uint)v;
        while (u >= 0x80) { bytes.Add((byte)(u | 0x80)); u >>= 7; }
        bytes.Add((byte)u);
        return [.. bytes];
    }

    private static byte[] Manga(string title, params float[] readChapters)
    {
        byte[] t = System.Text.Encoding.UTF8.GetBytes(title);
        var m = new List<byte> { 0x1A };
        m.AddRange(Varint(t.Length));
        m.AddRange(t);
        foreach (float n in readChapters)
        {
            byte[] chapter = [0x20, 0x01, 0x4D, .. BitConverter.GetBytes(n)];   // read=1, number=n
            m.AddRange([0x82, 0x01, (byte)chapter.Length, .. chapter]);
        }
        return [0x0A, .. Varint(m.Count), .. m];
    }

    private static MemoryStream Backup(IEnumerable<byte[]> mangas)
    {
        var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true))
            foreach (byte[] m in mangas)
                gz.Write(m);
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void A_backup_title_past_the_cap_is_dropped_not_matched()
    {
        using MemoryStream backup = Backup([
            Manga(new string('a', TachibkParser.MaxTitleLength + 1), 1),
            Manga(new string('b', TachibkParser.MaxTitleLength), 1),
        ]);
        TachibkManga kept = Assert.Single(TachibkParser.Parse(backup));
        Assert.Equal(TachibkParser.MaxTitleLength, kept.Title.Length);
    }

    private ReadStateService ReadStates() =>
        new(NullLogger<ReadStateService>.Instance, new ReadStateCacheService(), null!,
            new RenzoJsonService(null!, NullLogger<RenzoJsonService>.Instance), new ReadStateChangeNotifier());

    private List<decimal> OnDisk(string folder, string username) =>
        JsonSerializer.Deserialize<ImportSeriesSnapshot>(File.ReadAllText(Path.Combine(folder, "renzo.json")))!
            .UserReadStates!.Single(u => u.Username == username).Chapters.Select(c => c.ChapterNumber).OrderBy(n => n).ToList();

    [Fact]
    public void One_import_batch_merges_repeated_users_and_chapter_numbers()
    {
        string folder = Path.Combine(_root, "series");
        Directory.CreateDirectory(folder);
        static ChapterReadState Ch(decimal n) => new() { ChapterNumber = n, IsCompleted = true, Progress = 1 };

        ReadStates().ImportUserReadStates(folder, [
            new UserReadStateSnapshot { Username = "Levit", Chapters = [Ch(1), Ch(1)] },
            new UserReadStateSnapshot { Username = "Levit", Chapters = [Ch(1), Ch(2)] },
            new UserReadStateSnapshot { Username = "Other", Chapters = [Ch(9)] },
            new UserReadStateSnapshot { Username = "Levit", Chapters = [Ch(3)] },
        ]);

        var snap = JsonSerializer.Deserialize<ImportSeriesSnapshot>(File.ReadAllText(Path.Combine(folder, "renzo.json")))!;
        Assert.Single(snap.UserReadStates!, u => u.Username == "Levit");
        Assert.Equal([1m, 2m, 3m], OnDisk(folder, "Levit"));
        Assert.Equal([9m], OnDisk(folder, "Other"));
    }

    [Fact]
    public async Task Duplicate_backup_entries_for_one_series_merge_into_one_write()
    {
        UserEntity me = AddUser(UserLevel.User, "levit");
        string folder = Path.Combine(_root, "Solo Leveling");
        Directory.CreateDirectory(folder);
        var db = Db();
        db.Series.Add(new SeriesEntity { Id = Guid.NewGuid(), Title = "Solo Leveling", StoragePath = folder, OwnerId = me.Id });
        db.SaveChanges();

        // 300 entries for the same series (two spellings), each with a slice of chapters.
        var entries = Enumerable.Range(0, 300)
            .Select(i => Manga(i % 2 == 0 ? "Solo Leveling" : "Solo Levelling", i % 10 + 1, i % 10 + 2))
            .Append(Manga("Nothing Like It", 1))
            .ToList();
        using MemoryStream backup = Backup(entries);
        var file = new FormFile(backup, 0, backup.Length, "file", "b.tachibk");

        var controller = new ReaderController(null!, null!, ReadStates(), Db(), NullLogger<ReaderController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { Items = { ["User"] = me } } },
        };
        var result = Assert.IsType<OkObjectResult>((await controller.ImportBackupAsync(file)).Result);
        var dto = Assert.IsType<BackupImportResultDto>(result.Value);

        Assert.Equal(300, dto.MatchedSeries);
        Assert.Equal(11, dto.UpdatedChapters);            // chapters 1..11, each once
        Assert.Equal(["Nothing Like It"], dto.Unmatched);
        Assert.Equal(Enumerable.Range(1, 11).Select(n => (decimal)n), OnDisk(folder, "levit"));
    }
}
