using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RenzoBackend.Data;
using RenzoBackend.Extensions;
using RenzoBackend.Models;
using RenzoBackend.Models.Database;
using RenzoBackend.Models.Dto;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Images.Providers;
using RenzoBackend.Services.Reader.AutoBuy;
using RenzoBackend.Services.Series;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// 2026-11-15 audit: a stored cover URL was fetched from inside the host
/// network (SSRF into loopback/LAN, no size cap) and whatever came back was
/// served from our origin with the upstream Content-Type (stored XSS); and
/// auto-buy opened any stored chapter link in the logged-in sidecar browser.
/// </summary>
public class ImageFetchSafetyTests : IDisposable
{
    // ---- Only public addresses are fetched ----

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.10.0.1")]
    [InlineData("10.0.30.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.10")]
    [InlineData("169.254.169.254")]   // cloud metadata
    [InlineData("100.64.0.1")]        // CGNAT
    [InlineData("100.127.255.255")]
    [InlineData("0.0.0.0")]
    [InlineData("224.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]
    [InlineData("::ffff:127.0.0.1")]  // IPv4-mapped
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("2002:7f00:1::")]     // 6to4 of 127.0.0.1
    [InlineData("64:ff9b::a00:1")]    // NAT64 of 10.0.0.1
    [InlineData("::ffff:0:7f00:1")]   // IPv4-translated 127.0.0.1
    [InlineData("64:ff9b:1::808:808")] // local-use NAT64, whatever it embeds
    public void Private_and_special_addresses_are_not_public(string ip) =>
        Assert.False(OutboundUrlGuard.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.32.0.1")]
    [InlineData("100.128.0.1")]
    [InlineData("2606:4700::1111")]
    [InlineData("::ffff:8.8.8.8")]
    public void Internet_addresses_are_public(string ip) =>
        Assert.True(OutboundUrlGuard.IsPublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://127.0.0.1:9835/health")]
    [InlineData("http://[::1]:5050/")]
    [InlineData("http://localhost/")]
    [InlineData("http://2130706433/")]          // 127.0.0.1 as one number
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://8.8.8.8/x.jpg")]
    [InlineData("not a url")]
    public async Task Urls_handed_to_the_solver_or_sidecar_must_be_public_http(string url) =>
        Assert.False(await OutboundUrlGuard.IsPublicHttpUrlAsync(url));

    [Fact]
    public async Task A_public_literal_url_is_allowed() =>
        Assert.True(await OutboundUrlGuard.IsPublicHttpUrlAsync("https://8.8.8.8/cover.jpg"));

    [Fact]
    public async Task The_cover_client_never_connects_to_loopback()
    {
        // A real listener, so a connect that got through would be visible.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            int port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var http = new HttpClient(OutboundUrlGuard.CreateHandler());
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}/"));
            await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://localhost:{port}/"));
            Assert.False(listener.Pending());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_body_past_the_cap_is_refused()
    {
        using var src = new MemoryStream(new byte[2048]);
        using var dst = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => OutboundUrlGuard.CopyCappedAsync(src, dst, 1024, default));
        Assert.True(dst.Length <= 1024);
    }

    // ---- Only sniffed images are cached and served ----

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0x0D, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 1];
    private static readonly byte[] Html = Encoding.UTF8.GetBytes("<html><script>fetch('/api/settings')</script></html>");
    private static readonly byte[] Svg = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"/>");

    private sealed class Answer(byte[] body, string type) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var content = new ByteArrayContent(body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private readonly SqliteConnection _conn = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> _options;
    private readonly List<AppDbContext> _contexts = [];
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "renzo-img-" + Guid.NewGuid().ToString("N"));

    public ImageFetchSafetyTests()
    {
        _conn.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options;
        Db().Database.EnsureCreated();
    }

    public void Dispose()
    {
        _contexts.ForEach(c => c.Dispose());
        _conn.Dispose();
        try { Directory.Delete(_cache, true); } catch (DirectoryNotFoundException) { }
    }

    private AppDbContext Db()
    {
        var db = new AppDbContext(_options);
        _contexts.Add(db);
        return db;
    }

    /// <summary>Only the database and file cache are reached on an OK answer
    /// with no source id (no extension fallback, no Cloudflare solve).</summary>
    private UrlImageProvider Provider(AppDbContext db) => new(NullLogger<UrlImageProvider>.Instance, null!, db,
        Options.Create(new CacheOptions { CachePath = _cache, AgeInDays = 1 }), null!, null!);

    private ThumbCacheService Thumbs(AppDbContext db) => new(Options.Create(new CacheOptions { CachePath = _cache, AgeInDays = 1 }),
        NullLogger<ThumbCacheService>.Instance, db, null!, null!, null!, [Provider(db)]);

    private static EtagCacheEntity Entry(AppDbContext db, string url)
    {
        var e = new EtagCacheEntity { Key = Guid.NewGuid().ToString("N"), Url = url, NextUpdateUTC = DateTime.UtcNow };
        db.ETagCache.Add(e);
        db.SaveChanges();
        return e;
    }

    [Theory]
    [InlineData("html")]
    [InlineData("svg")]
    public async Task A_non_image_answer_is_not_cached(string kind)
    {
        var db = Db();
        EtagCacheEntity e = Entry(db, "https://cdn.example/cover.jpg");
        (byte[] body, string type) = kind == "html" ? (Html, "text/html") : (Svg, "image/svg+xml");

        await Provider(db).UpdateCacheWithRemoteAsync(e, new HttpClient(new Answer(body, type)));

        Assert.Equal("", e.Extension);
        Assert.Equal("", e.ContentType);
        Assert.False(Directory.Exists(_cache) && Directory.EnumerateFiles(_cache, "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task An_image_is_cached_with_its_sniffed_type_not_the_declared_one()
    {
        var db = Db();
        EtagCacheEntity e = Entry(db, "https://cdn.example/cover");

        await Provider(db).UpdateCacheWithRemoteAsync(e, new HttpClient(new Answer(Png, "text/html")));

        Assert.Equal(".png", e.Extension);
        Assert.Equal("image/png", e.ContentType);
    }

    [Fact]
    public async Task A_row_cached_as_html_before_the_fix_is_not_served()
    {
        var db = Db();
        EtagCacheEntity e = Entry(db, "https://cdn.example/old");
        e.Extension = ".html";
        e.ContentType = "text/html";
        e.Etag = "x";
        e.NextUpdateUTC = DateTime.UtcNow.AddDays(1);
        db.SaveChanges();
        string dir = Path.Combine(_cache, e.Key[..2]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, e.Key[2..] + ".html"), Html);

        var result = await Thumbs(db).ProcessKeyAsync(e.Key, "");

        Assert.Equal(HttpStatusCode.NotFound, result.StatusCode);
        Assert.Null(result.stream);
    }

    [Fact]
    public async Task A_cached_image_is_served_as_the_type_its_bytes_say()
    {
        var db = Db();
        EtagCacheEntity e = Entry(db, "https://cdn.example/cover2");
        e.Extension = ".png";
        e.ContentType = "text/html";   // a stale stored type is ignored
        e.Etag = "x";
        db.SaveChanges();
        string dir = Path.Combine(_cache, e.Key[..2]);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, e.Key[2..] + ".png"), Png);

        var result = await Thumbs(db).ProcessKeyAsync(e.Key, "");

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Equal("image/png", result.mimetype);
        Assert.Equal(0, result.stream!.Position);
        result.stream.Dispose();
    }

    [Fact]
    public void Served_covers_carry_the_lockdown_headers()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.AddImageSafetyHeaders("ab12\"\r\nX-Evil: 1", "image/webp");

        Assert.Equal("nosniff", ctx.Response.Headers["X-Content-Type-Options"]);
        Assert.Equal("default-src 'none'; sandbox", ctx.Response.Headers["Content-Security-Policy"]);
        Assert.Equal("inline; filename=\"ab12XEvil1.webp\"", ctx.Response.Headers["Content-Disposition"]);
    }

    // ---- Client-sent covers on POST /api/serie ----

    [Fact]
    public void Add_keeps_source_covers_and_drops_anything_else()
    {
        string[] sent =
        [
            "https://cdn.example/a.jpg", "http://cdn.example/b.jpg", "/api/image/0123abcd", "storage://Series/cover.jpg",
            "ext://../../renzo.db", "file:///etc/passwd", "javascript:alert(1)", "gopher://x/",
        ];
        var dto = new AugmentedResponseDto { Series = sent.Select(u => new ProviderSeriesDetails { ThumbnailUrl = u }).ToList() };

        SeriesCommandService.SanitizeAddThumbnails(dto);

        Assert.Equal(sent.Take(4), dto.Series.Take(4).Select(s => s.ThumbnailUrl));
        Assert.All(dto.Series.Skip(4), s => Assert.Null(s.ThumbnailUrl));
    }

    // ---- Auto-buy only opens the source's own site ----

    [Theory]
    [InlineData("ezmanga.org", "ezmanga.org", true)]
    [InlineData("www.ezmanga.org", "ezmanga.org", true)]
    [InlineData("cdn.ezmanga.org", "ezmanga.org", true)]
    [InlineData("ezmanga.org", "https://www.ezmanga.org/", true)]
    [InlineData("evilezmanga.org", "ezmanga.org", false)]
    [InlineData("ezmanga.org.evil.com", "ezmanga.org", false)]
    [InlineData("127.0.0.1", "ezmanga.org", false)]
    [InlineData("ezmanga.org", "", false)]
    public void Auto_buy_host_must_be_the_source_site(string host, string domain, bool expected) =>
        Assert.Equal(expected, ChapterAutoBuyService.IsOnSiteHost(host, domain));

    // ---- streamed reader pages are never served as a document ----

    [Theory]
    [InlineData("text/html", "application/octet-stream")]
    [InlineData("image/svg+xml", "application/octet-stream")]
    [InlineData("image/avif", "image/avif")]          // not sniffed, but a plain raster type
    [InlineData("image/webp; charset=x", "image/webp")]
    [InlineData(null, "application/octet-stream")]
    public void An_unsniffable_page_keeps_only_a_plain_image_type(string? upstream, string expected)
    {
        using var body = new MemoryStream(Encoding.UTF8.GetBytes("<script>alert(1)</script>"));
        Assert.Equal(expected, HttpExtensions.SafePageContentType(body, upstream));
    }

    [Fact]
    public void A_sniffed_page_image_wins_over_the_upstream_type()
    {
        using var png = new MemoryStream([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
        Assert.Equal("image/png", HttpExtensions.SafePageContentType(png, "text/html"));
        Assert.Equal(0, png.Position);   // left where File() will read from
    }
}
