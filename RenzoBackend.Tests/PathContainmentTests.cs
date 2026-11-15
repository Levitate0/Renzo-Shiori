using Microsoft.Extensions.Configuration;
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
using RenzoBackend.Services.Opds;
using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-15 audit, "paths-files": names a client chose were joined onto the
/// library with Path.Combine and nothing else — an OPDS chapter download served
/// any file on the host, a filename posted with an add later made "delete
/// downloads" delete anywhere, storage:// and CBZ entries escaped their folders,
/// and OPDS/match endpoints reached other users' series.
/// </summary>
public class PathContainmentTests : IDisposable
{
    private readonly string _root;
    private readonly string _library;
    private readonly string _series;
    private readonly string _outside;

    public PathContainmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "renzo-paths-" + Guid.NewGuid().ToString("N"));
        _library = Path.Combine(_root, "library");
        _series = Path.Combine(_library, "owner", "Series");
        Directory.CreateDirectory(_series);
        File.WriteAllText(Path.Combine(_series, "[Src][en] Series 001.cbz"), "chapter");
        // Stands in for renzo.db / appsettings.json next to the library.
        _outside = Path.Combine(_root, "renzo.db");
        File.WriteAllText(_outside, "secret");

        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        Db().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _contexts.ForEach(c => c.Dispose());
        _conn.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private SettingsDto Settings => new() { StorageFolder = _library };

    // ---- the helper itself ----

    [Theory]
    [InlineData("a.cbz", true)]
    [InlineData("[Src][en] Title (x) 001.cbz", true)]
    [InlineData("..cbz", true)]
    [InlineData("", false)]
    [InlineData(".", false)]
    [InlineData("..", false)]
    [InlineData("../renzo.db", false)]
    [InlineData("sub/a.cbz", false)]
    [InlineData("sub\\a.cbz", false)]
    [InlineData("/etc/passwd", false)]
    public void Only_bare_file_names_are_safe(string name, bool safe)
    {
        Assert.Equal(safe, PathSafety.IsSafeFileName(name));
    }

    [Theory]
    [InlineData("owner/Series", true)]
    [InlineData("owner/Manga/Series", true)]
    [InlineData("owner/..hidden", true)]
    [InlineData("owner/../../etc", false)]
    [InlineData("..", false)]
    [InlineData("/etc", false)]
    [InlineData("\\etc", false)]
    [InlineData("C:\\Windows", false)]
    public void Relative_paths_may_not_climb(string path, bool safe)
    {
        Assert.Equal(safe, PathSafety.IsSafeRelativePath(path));
    }

    [Fact]
    public void Contained_path_refuses_climbing_absolute_and_the_root_itself()
    {
        Assert.NotNull(PathSafety.ContainedPath(_library, "owner/Series"));
        Assert.Null(PathSafety.ContainedPath(_library, "../renzo.db"));
        Assert.Null(PathSafety.ContainedPath(_library, _outside));
        Assert.Null(PathSafety.ContainedPath(_library, "owner/../.."));
        Assert.Null(PathSafety.ContainedPath(_library, "."));
        // A sibling folder that merely shares the prefix is outside.
        Assert.False(PathSafety.IsInside(_library, _library + "-evil/x"));
    }

    // ---- C2: OPDS chapter download ----

    [Theory]
    [InlineData("../../renzo.db")]
    [InlineData("../../../renzo.db")]
    [InlineData("..")]
    [InlineData("")]
    public void Chapter_path_never_leaves_the_series_folder(string requested)
    {
        Assert.Null(Settings.ResolveChapterPath("owner/Series", requested));
    }

    [Fact]
    public void Chapter_path_refuses_an_absolute_name()
    {
        Assert.Null(Settings.ResolveChapterPath("owner/Series", _outside));
        Assert.Null(Settings.ResolveChapterFilePath("owner/Series", _outside));
    }

    [Fact]
    public void Chapter_path_refuses_a_series_folder_outside_the_library()
    {
        Assert.Null(Settings.ResolveSeriesAbsolutePath("../"));
        Assert.Null(Settings.ResolveChapterPath("..", "renzo.db"));
        Assert.Null(Settings.ResolveChapterFilePath("owner/../..", "renzo.db"));
    }

    [Fact]
    public void Chapter_path_still_finds_a_real_chapter()
    {
        string? p = Settings.ResolveChapterPath("owner/Series", "[Src][en] Series 001.cbz");
        Assert.Equal(Path.Combine(_series, "[Src][en] Series 001.cbz"), p);
        // The old extension fallback still works inside the folder.
        Assert.NotNull(Settings.ResolveChapterPath("owner/Series", "[Src][en] Series 001.cbr"));
    }

    [Fact]
    public void Opds_download_only_names_a_chapter_the_series_holds()
    {
        var s = new SeriesEntity
        {
            Sources = [new SeriesProviderEntity { Chapters = [new Chapter { Number = 1, Filename = "[Src][en] Series 001.cbz" }, new Chapter { Number = 2 }] }],
        };
        Assert.Equal("[Src][en] Series 001.cbz", OpdsFeedService.KnownChapterFilename(s, "[src][EN] series 001.CBZ"));
        Assert.Null(OpdsFeedService.KnownChapterFilename(s, "../../renzo.db"));
        Assert.Null(OpdsFeedService.KnownChapterFilename(s, ""));
    }

    // ---- H6: add-series input and delete sinks ----

    private static AugmentedResponseDto Add(string storage, params string?[] filenames) => new()
    {
        StorageFolderPath = storage,
        Series = [new ProviderSeriesDetails { Chapters = filenames.Select((f, i) => new Chapter { Number = i + 1, Filename = f }).ToList() }],
    };

    [Theory]
    [InlineData("../renzo.db")]
    [InlineData("/app/appsettings.json")]
    [InlineData("sub/x.cbz")]
    [InlineData("..")]
    public void Add_rejects_a_chapter_filename_with_a_path(string filename)
    {
        Assert.Throws<ArgumentException>(() => SeriesCommandService.ValidateAddPaths(Add("Series", "ok.cbz", filename)));
    }

    [Theory]
    [InlineData("../../etc")]
    [InlineData("Series/../../..")]
    [InlineData("..\\..\\x")]
    public void Add_rejects_a_storage_folder_that_climbs(string storage)
    {
        Assert.Throws<ArgumentException>(() => SeriesCommandService.ValidateAddPaths(Add(storage)));
    }

    [Fact]
    public void Add_accepts_what_the_wizard_and_import_send()
    {
        // The web wizard sends {StorageFolder}/Title; import sends bare names.
        SeriesCommandService.ValidateAddPaths(Add(Path.Combine(_library, "Series"), "[Src][en] Series 001.cbz", null, ""));
        SeriesCommandService.ValidateAddPaths(Add("Manga/Series"));
        SeriesCommandService.ValidateAddPaths(Add(""));
    }

    [Theory]
    [InlineData("owner/Series", true)]
    [InlineData("owner/../../outside", false)]
    [InlineData("..", false)]
    [InlineData("", false)]
    public void Final_storage_path_must_sit_inside_the_library(string path, bool ok)
    {
        Assert.Equal(ok, SeriesCommandService.IsSafeSeriesStoragePath(Settings, path));
    }

    [Fact]
    public void Deleting_a_series_with_files_never_deletes_outside_it()
    {
        var series = new SeriesEntity
        {
            Title = "Series", StoragePath = "owner/Series",
            Sources = [new SeriesProviderEntity { Chapters = [new Chapter { Number = 1, Filename = "../../../renzo.db" }, new Chapter { Number = 2, Filename = _outside }] }],
        };
        series.DeletePhysicalSeries(Settings, null);
        Assert.True(File.Exists(_outside));

        var escaped = new SeriesEntity
        {
            Title = "Escaped", StoragePath = "..",
            Sources = [new SeriesProviderEntity { Chapters = [new Chapter { Number = 1, Filename = "renzo.db" }] }],
        };
        escaped.DeletePhysicalSeries(Settings, null);
        Assert.True(File.Exists(_outside));
    }

    [Fact]
    public void Deleting_a_series_with_files_still_deletes_its_own_files()
    {
        var series = new SeriesEntity
        {
            Title = "Series", StoragePath = "owner/Series",
            Sources = [new SeriesProviderEntity { Chapters = [new Chapter { Number = 1, Filename = "[Src][en] Series 001.cbz" }] }],
        };
        series.DeletePhysicalSeries(Settings, null);
        Assert.False(Directory.Exists(_series));
    }

    // ---- L18: other users' series ----

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];

    private AppDbContext Db()
    {
        var db = new AppDbContext(_options);
        _contexts.Add(db);
        return db;
    }

    /// <summary>Only the database is reached before the ownership check.</summary>
    private SeriesProviderService Providers(AppDbContext db) =>
        new(db, null!, null!, null!, NullLogger<SeriesProviderService>.Instance, null!);

    private (Guid owner, Guid unknownProvider) SeedUnknownSource()
    {
        var db = Db();
        Guid owner = Guid.NewGuid();
        var series = new SeriesEntity { Id = Guid.NewGuid(), Title = "Theirs", StoragePath = "owner/Series", OwnerId = owner };
        var unknown = new SeriesProviderEntity
        {
            Id = Guid.NewGuid(), SeriesId = series.Id, Provider = "Unknown", Scanlator = "", Title = "Theirs",
            Language = "en", IsUnknown = true,
            Chapters = [new Chapter { Number = 1, Filename = "[Src][en] Series 001.cbz" }],
        };
        db.Series.Add(series);
        db.SeriesProviders.Add(unknown);
        db.SaveChanges();
        return (owner, unknown.Id);
    }

    [Fact]
    public async Task Match_lookup_does_not_show_another_users_files()
    {
        var (owner, unknown) = SeedUnknownSource();
        Assert.Null(await Providers(Db()).GetMatchAsync(unknown, Guid.NewGuid(), false));
        Assert.NotNull(await Providers(Db()).GetMatchAsync(unknown, owner, false));
        Assert.NotNull(await Providers(Db()).GetMatchAsync(unknown, Guid.NewGuid(), true));   // Owner level
    }

    [Fact]
    public async Task Match_apply_does_not_touch_another_users_series()
    {
        var (_, unknown) = SeedUnknownSource();
        var pm = new ProviderMatchDto { Id = unknown, MatchInfos = [], Chapters = [] };
        Assert.False(await Providers(Db()).SetMatchAsync(pm, Guid.NewGuid(), false));
        Assert.Single(await Db().SeriesProviders.Where(p => p.Id == unknown).ToListAsync());
    }

    [Theory]
    [InlineData(UserLevel.User, false)]
    [InlineData(UserLevel.Manager, false)]
    [InlineData(UserLevel.Admin, false)]
    [InlineData(UserLevel.Owner, true)]
    public void Opds_reaches_another_users_series_only_as_owner(UserLevel level, bool allowed)
    {
        var user = new UserEntity { Id = Guid.NewGuid(), Username = "u", Level = level, OpdsPath = "p" };
        Assert.Equal(allowed, OpdsController.CanAccess(user, new SeriesEntity { OwnerId = Guid.NewGuid() }));
        Assert.True(OpdsController.CanAccess(user, new SeriesEntity { OwnerId = user.Id }));
    }

    // ---- second pass: stored paths are contained at the write sinks too ----

    [Fact]
    public void A_hash_cache_for_a_climbing_storage_path_stays_in_the_hashes_folder()
    {
        string runtime = Path.Combine(_root, "runtime");
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["runtimeDirectory"] = runtime }).Build();
        var hashes = new HashCacheService(config);

        hashes.SaveChapterHash("../../escaped", new RenzoBackend.Models.ReadState.ChapterHashCache { ArchiveFilename = "a.cbz" });

        Assert.False(File.Exists(Path.Combine(_root, "escaped.json")));
        Assert.False(File.Exists(Path.Combine(runtime, "escaped.json")));
        string written = Assert.Single(Directory.GetFiles(Path.Combine(runtime, "hashes"), "*.json", SearchOption.AllDirectories));
        Assert.True(PathSafety.IsInside(Path.Combine(runtime, "hashes"), written));
        Assert.NotNull(hashes.GetChapterHash("../../escaped", "a.cbz"));   // still usable
    }
}
