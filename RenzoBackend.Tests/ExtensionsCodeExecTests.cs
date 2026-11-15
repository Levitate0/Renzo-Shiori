using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Mihon.ExtensionsBridge.Core.Utilities;
using RenzoBackend.Controllers;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Reader;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-15 audit: loading an extension runs its code inside the sidecar, so
/// who may put an APK there, and what an APK's manifest may name, are the
/// gates. Plus the shared things a plain user could change for everyone:
/// extension settings, the ComicVine key and the streamed-page cache.
/// </summary>
public class ExtensionsCodeExecTests
{
    // ---- who may install / sideload / write shared settings ----

    [Theory]
    [InlineData(typeof(ProviderController), "InstallProviderFromFileAsync", UserLevel.Owner)]
    [InlineData(typeof(ProviderController), "InstallProvider", UserLevel.Manager)]
    [InlineData(typeof(ProviderController), "SetPreferencesAsync", UserLevel.Owner)]
    [InlineData(typeof(ExtensionsController), "SideloadAsync", UserLevel.Owner)]
    [InlineData(typeof(ScrobblerController), "SaveComicVineApiKey", UserLevel.Owner)]
    [InlineData(typeof(ProviderController), "GetPreferencesAsync", UserLevel.Owner)]   // settings can hold a source login
    [InlineData(typeof(SiteAuthController), "Save", UserLevel.Owner)]                  // the one shared cookie jar
    [InlineData(typeof(SiteAuthController), "SaveCookie", UserLevel.Owner)]
    [InlineData(typeof(SiteAuthController), "Relogin", UserLevel.Owner)]
    [InlineData(typeof(SiteAuthController), "Delete", UserLevel.Owner)]
    public void Endpoint_requires_level(Type controller, string action, UserLevel expected)
    {
        MethodInfo m = controller.GetMethod(action)!;
        var attr = m.GetCustomAttribute<RequireUserLevelAttribute>();
        Assert.NotNull(attr);
        // The level is private: prove it by running the filter at the levels either side.
        Assert.Null(Run(attr!, expected));
        if (expected > UserLevel.User)
            Assert.IsType<StatusCodeResult>(Run(attr!, expected - 1));
    }

    private static IActionResult? Run(RequireUserLevelAttribute attr, UserLevel level)
    {
        var http = new DefaultHttpContext();
        http.Items["AuthEnabled"] = true;
        http.Items["User"] = new UserEntity { Id = Guid.NewGuid(), Username = "u", Level = level, OpdsPath = "p" };
        var ctx = new Microsoft.AspNetCore.Mvc.Filters.AuthorizationFilterContext(
            new ActionContext(http, new Microsoft.AspNetCore.Routing.RouteData(),
                new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor()),
            []);
        attr.OnAuthorization(ctx);
        return ctx.Result;
    }

    private static ControllerContext As(UserLevel level) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            Items =
            {
                ["AuthEnabled"] = true,
                ["User"] = new UserEntity { Id = Guid.NewGuid(), Username = "u", Level = level, OpdsPath = "p" },
            },
        },
    };

    [Theory]
    [InlineData(UserLevel.Manager)]
    [InlineData(UserLevel.Admin)]
    public async Task Force_reinstall_below_owner_is_refused_before_anything_runs(UserLevel level)
    {
        // Services are null: reaching the install would throw, not return 403.
        var c = new ProviderController(NullLogger<ProviderController>.Instance, null!, null!, null!, null!)
        {
            ControllerContext = As(level),
        };
        IActionResult r = await c.InstallProvider("eu.kanade.tachiyomi.extension.en.x", force: true);
        Assert.Equal(403, Assert.IsType<ObjectResult>(r).StatusCode);
    }

    // ---- an APK manifest cannot name a path ----

    [Theory]
    [InlineData("eu.kanade.tachiyomi.extension.en.mangadex", true)]
    [InlineData("eu.kanade.tachiyomi.extension.all_x.baiheverse2", true)]
    [InlineData("single", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("../../etc", false)]
    [InlineData("a..b", false)]
    [InlineData(".a", false)]
    [InlineData("a.", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("/abs.path", false)]
    [InlineData("C:x", false)]
    [InlineData("1bad.start", false)]
    [InlineData("eu.kanade.tachiyomi.extension.en.x/../../y", false)]
    public void Package_must_be_a_java_package_id(string package, bool ok)
    {
        Assert.Equal(ok, ExtensionIdentity.IsValidPackageName(package));
    }

    [Theory]
    [InlineData("1.4.12", true)]
    [InlineData("1.4.1-beta+2", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("1.4/../../x", false)]
    [InlineData("1\\2", false)]
    public void Version_has_no_path_characters(string version, bool ok)
    {
        Assert.Equal(ok, ExtensionIdentity.IsValidVersionName(version));
    }

    [Fact]
    public void Extension_folders_stay_under_the_extensions_root()
    {
        string root = Path.Combine(Path.GetTempPath(), "ext-root");
        Assert.True(ExtensionIdentity.IsUnder(root, Path.Combine(root, "tachiyomi-en.x", "1.4.1_Local")));
        Assert.False(ExtensionIdentity.IsUnder(root, Path.Combine(root, "..", "elsewhere")));
        Assert.False(ExtensionIdentity.IsUnder(root, root + "-sibling"));
        Assert.False(ExtensionIdentity.IsUnder(root, "/etc"));
        Assert.False(ExtensionIdentity.IsUnder(root, root));
    }

    // ---- the streamed-page cache: users clear their own, the Owner clears all ----

    private static (StreamImageCache cache, ReaderController controller) Reader(UserLevel level, out Guid me, out Guid other)
    {
        var cache = new StreamImageCache();
        var preview = new ReaderPreviewService(null!, null!, null!, null!, cache, null!, null!,
            NullLogger<ReaderPreviewService>.Instance, null!);
        var ctx = As(level);
        me = ((UserEntity)ctx.HttpContext.Items["User"]!).Id;
        other = Guid.NewGuid();
        cache.Set("lib:img:a:1:0", [1], "image/jpeg", me);
        cache.Set("lib:img:a:1:1", [1], "image/jpeg", me);
        cache.Set("lib:img:b:1:0", [1], "image/jpeg", other);
        cache.Set("pv:img:c:0:0", [1], "image/jpeg");
        var controller = new ReaderController(null!, preview, null!, null!, NullLogger<ReaderController>.Instance)
        {
            ControllerContext = ctx,
        };
        return (cache, controller);
    }

    [Theory]
    [InlineData(UserLevel.User)]
    [InlineData(UserLevel.Admin)]
    public void Clear_below_owner_drops_only_the_callers_pages(UserLevel level)
    {
        var (cache, c) = Reader(level, out _, out _);
        Assert.IsType<OkObjectResult>(c.ClearStreamCache());
        Assert.False(cache.TryGet("lib:img:a:1:0", out _));
        Assert.False(cache.TryGet("lib:img:a:1:1", out _));
        Assert.True(cache.TryGet("lib:img:b:1:0", out _));   // another reader's page
        Assert.True(cache.TryGet("pv:img:c:0:0", out _));    // nobody's in particular
    }

    [Fact]
    public void Clear_as_owner_drops_everything()
    {
        var (cache, c) = Reader(UserLevel.Owner, out _, out _);
        c.ClearStreamCache();
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Trim_below_owner_drops_only_the_callers_pages()
    {
        var (cache, c) = Reader(UserLevel.User, out Guid me, out Guid other);
        var series = Guid.NewGuid();
        cache.Set($"lib:img:{series}:5:0", [1], "image/jpeg", me);
        cache.Set($"lib:img:{series}:6:0", [1], "image/jpeg", other);
        cache.Set($"lib:img:{series}:7:0", [1], "image/jpeg");
        Assert.IsType<OkObjectResult>(c.TrimStreamCache(new ReaderController.TrimStreamCacheRequest { SeriesId = series, Keep = [-1m] }));
        Assert.False(cache.TryGet($"lib:img:{series}:5:0", out _));
        Assert.True(cache.TryGet($"lib:img:{series}:6:0", out _));   // another reader's page
        Assert.True(cache.TryGet($"lib:img:{series}:7:0", out _));   // unattributed
    }

    [Fact]
    public void Trim_as_owner_drops_every_readers_pages()
    {
        var (cache, c) = Reader(UserLevel.Owner, out Guid me, out Guid other);
        var series = Guid.NewGuid();
        cache.Set($"lib:img:{series}:5:0", [1], "image/jpeg", me);
        cache.Set($"lib:img:{series}:6:0", [1], "image/jpeg", other);
        cache.Set($"lib:img:{series}:7:0", [1], "image/jpeg", other);
        c.TrimStreamCache(new ReaderController.TrimStreamCacheRequest { SeriesId = series, Keep = [7m] });
        Assert.False(cache.TryGet($"lib:img:{series}:5:0", out _));
        Assert.False(cache.TryGet($"lib:img:{series}:6:0", out _));
        Assert.True(cache.TryGet($"lib:img:{series}:7:0", out _));
    }
}
