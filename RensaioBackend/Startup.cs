using RensaioBackend.Data;
using RensaioBackend.Hubs;
using RensaioBackend.Models;
using RensaioBackend.Services;
using RensaioBackend.Services.Auth;
using RensaioBackend.Services.Background;
using RensaioBackend.Services.Settings;
using RensaioBackend.Utils;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.FileProviders;
using Mihon.ExtensionsBridge.Core.Extensions;
using Mihon.ExtensionsBridge.Models.Configuration;
using Serilog;
using Serilog.Extensions.Logging;
using System.Threading.RateLimiting;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace RensaioBackend
{
    public class Startup
    {
        public IConfiguration Configuration { get; }
        private ILogger? _logger;

        public ILogger Logger
        {
            get
            {
                if (_logger == null)
                    _logger = LoggerInfrastructure.CreateAppLogger<Startup>(EnvironmentSetup.AppRensaio);
                return _logger;
            }
        }

        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public void ConfigureServices(IServiceCollection services)
        {
            Serilog.ILogger logger = Log.Logger;
            services.AddSerilog(logger, false, null);
            services.Replace(ServiceDescriptor.Singleton<ILoggerFactory>(sp =>
            {
                return new LibraryTaggingLoggerFactory(new SerilogLoggerFactory(Log.Logger, false));
            }));

            Logger.LogInformation("Initializing Rensaiō...");

            services.AddOpenApi();
            services.AddControllers().AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.PropertyNamingPolicy = null;
            });
            services.AddEndpointsApiExplorer();
            services.AddSwaggerGen();
            services.Configure<HostOptions>(opts => opts.ShutdownTimeout = TimeSpan.FromSeconds(10));

            // CORS policy is resolved per request by DynamicCorsPolicyProvider from the
            // WebUI-editable AllowedOrigins setting (Settings → Security), so origin
            // changes apply live. Seed the runtime snapshot from the legacy top-level
            // appsettings.json keys so pre-UI configs keep working until first save;
            // SettingsService overwrites the snapshot with DB-persisted values on load.
            RuntimeSecuritySettings.Set(
                Configuration.GetSection("AllowedOrigins").Get<string[]>(),
                Configuration.GetValue<int>("Authentication:SessionExpirationHours", 24),
                Configuration.GetValue<int>("Authentication:RememberMeExpirationDays", 90));
            services.AddCors();
            services.AddSingleton<ICorsPolicyProvider, DynamicCorsPolicyProvider>();

            // Rate limiting: IP-scoped fixed window specifically for the login endpoint,
            // as a brute-force mitigation. RemoteIpAddress reflects the real client IP
            // (not Cloudflare's edge IP) because UseForwardedHeaders() runs before this
            // is ever evaluated per-request, reading X-Forwarded-For from the tunnel.
            services.AddRateLimiter(options =>
            {
                options.AddPolicy("login", context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        partitionKey: context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                        factory: _ => new FixedWindowRateLimiterOptions
                        {
                            Window = TimeSpan.FromMinutes(1),
                            PermitLimit = 5,
                            QueueLimit = 0
                        }));

                options.OnRejected = async (context, token) =>
                {
                    context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    context.HttpContext.Response.ContentType = "application/json";
                    await context.HttpContext.Response.WriteAsync(
                        "{\"error\":\"Too many login attempts. Please wait a minute and try again.\"}", token);
                };
            });

            services.AddResponseCompression(options =>
            {
                options.EnableForHttps = true;
                options.Providers.Add<BrotliCompressionProvider>();
                services.AddScoped<GzipCompressionProvider>();
                options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/json"]);
            });
            services.AddSignalR();
            services.AddHttpContextAccessor();
            services.AddMemoryCache();
            services.AddDataProtection();
            services.Configure<Paths>(a =>
            {
                a.BridgeFolder = Configuration.GetValue<string>("BridgeFolder", "extensions");
                a.TempFolder = Configuration.GetValue<string>("TempFolder", string.Empty);
            });
            services.Configure<CacheOptions>(options =>
            {
                options.CachePath = Configuration.GetValue<string>("ThumbCacheFolder", "thumbs");
                options.AgeInDays = Configuration.GetValue<int>("CacheCheckInDays", 7);
            });

            services.AddExtensionsBridge();

            // Add consolidated services
            services.AddImportService();
            services.AddSeriesServices();
            services.AddJobServices();
            services.AddProviderServices();
            services.AddSearchServices();
            services.AddDownloadServices();
            services.AddHelperServices();
            services.AddRensaioJsonService();   // Singleton: shared per-file lock for rensaio.json atomicity
            services.AddBackgroundServices();
            services.AddAuthServices();
            services.AddReadStateServices();
            services.AddOpdsServices();
            services.AddScrobblingServices(Configuration);
            services.AddMcpServices();

            // Configure ForwardedHeaders to support reverse proxy SSL termination.
            // Without this, Kestrel is unaware of the original HTTPS scheme when deployed
            // behind a reverse proxy (Nginx, Traefik, Caddy, etc.), causing redirects to
            // incorrectly use http:// instead of https://.
            services.Configure<ForwardedHeadersOptions>(options =>
            {
                options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                // Clear default restrictions to accept forwarded headers from any proxy.
                // This is safe when the app is always deployed behind a trusted reverse proxy.
                options.KnownNetworks.Clear();
                options.KnownProxies.Clear();
            });

            // Register AppDbContext with SQLite provider, using the connection string from configuration (now points to runtime/rensaio.db)
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(Configuration.GetConnectionString("DefaultConnection")));
            services.AddHostedService<StartupHostedService>();
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            // Must be first: reads X-Forwarded-Proto/For headers from reverse proxy so that
            // all subsequent middleware (redirects, HSTS, etc.) use the correct scheme and IP.
            // Without this, Response.Redirect() generates http:// URLs even when the client
            // connected over HTTPS, causing ERR_FR_REDIRECTION_FAILURE in strict HTTPS clients.
            // It also matters for rate limiting: the "login" policy partitions by
            // Connection.RemoteIpAddress, which this middleware rewrites from
            // X-Forwarded-For - without it, every request behind Cloudflare Tunnel would
            // appear to come from the same edge IP and share one rate-limit bucket.
            app.UseForwardedHeaders();

            if (env.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseResponseCompression();
            //app.UseSerilogRequestLogging();
            // Apply CORS policy before other middleware
            app.UseCors();

            // Allow both HTTP and HTTPS - UseHttpsRedirection is conditionally applied
            if (Configuration.GetValue("UseHttpsRedirection", false))
            {
                app.UseHttpsRedirection();
            }

            // Configure static file serving with proper MIME types for .txt files
            var provider = new FileExtensionContentTypeProvider();
            // Add or update .txt mapping to ensure react/next.js fragments work
            provider.Mappings[".txt"] = "text/plain; charset=utf-8";

            var webRoot = Path.Combine(EnvironmentSetup.Configuration!["runtimeDirectory"]!, "wwwroot");

            // Static files MUST be registered before UseRouting. The OpdsController defines a
            // catch-all attribute route [HttpGet("/{opdsPath}")] that matches any single-segment
            // URL at the root (e.g. /library, /favicon.ico, /settings). If UseRouting runs first
            // those requests are bound to the OPDS endpoint and never reach UseStaticFiles, which
            // returns 404 because the OPDS user lookup fails.

            // Next.js static export uses trailingSlash routing: /library => wwwroot/library/index.html.
            // UseDefaultFiles only rewrites to index.html when the request path ends with '/'.
            // Without this rewrite, direct navigation to /library, /settings, etc. returns 404
            // because no controller matches and UseStaticFiles can't find a literal file named "library".
            app.Use(async (context, next) =>
            {
                var path = context.Request.Path.Value;
                if (!string.IsNullOrEmpty(path) && path.Length > 1 && !path.EndsWith('/') && !Path.HasExtension(path))
                {
                    var relative = path.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                    var dirPath = Path.Combine(webRoot, relative);
                    if (Directory.Exists(dirPath))
                    {
                        context.Request.Path = path + "/";
                    }
                }
                await next();
            });

            // Serve default files (index.html)
            app.UseDefaultFiles(new DefaultFilesOptions
            {
                DefaultFileNames = new List<string> { "index.html" },
                FileProvider = new PhysicalFileProvider(webRoot)
            });

            // Serve static files with custom content type provider
            app.UseStaticFiles(new StaticFileOptions
            {
                ContentTypeProvider = provider,
                ServeUnknownFileTypes = false, // Only serve files with known MIME types for security
                OnPrepareResponse = context =>
                {
                    // Add caching headers for static files
                    var headers = context.Context.Response.Headers;

                    // HTML entry points (index.html, library/index.html, ...) aren't
                    // content-hashed and change on every deploy — always revalidate with the
                    // server instead of letting the browser keep serving a stale page (and its
                    // stale JS) for up to a day after a redeploy.
                    if (context.File.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
                    {
                        headers.CacheControl = "no-cache";
                    }
                    // Service worker + manifest control the PWA update cycle — a cached
                    // copy here delays every future frontend update reaching clients.
                    else if (context.File.Name.Equals("sw.js", StringComparison.OrdinalIgnoreCase)
                             || context.File.Name.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase))
                    {
                        headers.CacheControl = "no-cache";
                    }
                    // Next.js content-hashes filenames under _next/static/, so a cached copy
                    // can never go stale — safe to cache aggressively and mark immutable.
                    else if (context.Context.Request.Path.Value?.Contains("/_next/static/", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        headers.CacheControl = "public, max-age=31536000, immutable"; // 1 year
                    }
                    // Cache .txt files for a shorter period (1 hour) since they might change more frequently
                    else if (context.File.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                    {
                        headers.CacheControl = "public, max-age=3600"; // 1 hour
                    }
                    // Cache other static files for longer (1 day)
                    else
                    {
                        headers.CacheControl = "public, max-age=86400"; // 24 hours
                    }
                }
            });

            // Baseline security response headers. Cheap, broad-coverage hardening that
            // doesn't require any per-endpoint work:
            //   - X-Content-Type-Options: stops the browser from MIME-sniffing a response
            //     into a different type than declared (mitigates some XSS/content-confusion
            //     attacks via uploaded/served files).
            //   - X-Frame-Options / frame-ancestors: stops the app being embedded in a
            //     hidden iframe on another site (clickjacking) now that it's public.
            //   - Referrer-Policy: avoids leaking full URLs (which could include the image
            //     token query string) to third-party sites via the Referer header when a
            //     user clicks an outbound link (e.g. a "view on source site" link).
            app.Use(async (context, next) =>
            {
                context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
                context.Response.Headers.Append("X-Frame-Options", "SAMEORIGIN");
                context.Response.Headers.Append("Referrer-Policy", "strict-origin-when-cross-origin");
                await next();
            });

            // Order matters for the following middleware
            app.UseRouting();

            // Must come after UseRouting (so [EnableRateLimiting] endpoint metadata is
            // available) and before UseAuthMiddleware/UseEndpoints (so a rate-limited
            // request never reaches the login handler at all).
            app.UseRateLimiter();

            // Auth middleware - after routing, before endpoints
            app.UseAuthMiddleware();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapGet("/", context =>
                {
                    // Trailing slash matches Next.js static export convention and lets
                    // UseDefaultFiles serve wwwroot/library/index.html directly.
                    context.Response.Redirect("/library/", permanent: false); // 302 Temporary
                    return Task.CompletedTask;
                });
                endpoints.MapControllers();
                endpoints.MapHub<ProgressHub>("/progress");
            });
            // Configure HSTS (HTTP Strict Transport Security)
            if (!env.IsDevelopment())
            {
                app.UseHsts(); // Adds HSTS header in production
            }

            Logger.LogInformation("Initializing Complete.");
        }
    }
    /*
    public sealed class RequestLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestLoggingMiddleware> _logger;

        public RequestLoggingMiddleware(
            RequestDelegate next,
            ILogger<RequestLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            var request = context.Request;

            var clientIp =
                context.Request.Headers["X-Forwarded-For"].FirstOrDefault()
                ?? context.Connection.RemoteIpAddress?.ToString();

            var correlationId =
                context.TraceIdentifier;

            using var scope = _logger.BeginScope(new Dictionary<string, object?>
            {
                ["TraceId"] = correlationId,
                ["ClientIp"] = clientIp,
                ["Path"] = request.Path.Value,
                ["Method"] = request.Method
            });

            try
            {
                await _next(context);

                sw.Stop();

                _logger.LogInformation(
                    "HTTP {Method} {Path}{QueryString} responded {StatusCode} in {ElapsedMs} ms from {ClientIp}",
                    request.Method,
                    request.Path,
                    request.QueryString,
                    context.Response.StatusCode,
                    sw.ElapsedMilliseconds,
                    clientIp);
            }
            catch (Exception ex)
            {
                sw.Stop();

                _logger.LogError(
                    ex,
                    "HTTP {Method} {Path}{QueryString} failed after {ElapsedMs} ms from {ClientIp}",
                    request.Method,
                    request.Path,
                    request.QueryString,
                    sw.ElapsedMilliseconds,
                    clientIp);

                throw;
            }
        }
    }**/
}
