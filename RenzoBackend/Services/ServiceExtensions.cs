using RenzoBackend.Extensions;
using RenzoBackend.Migration;
using RenzoBackend.Models.Enums;
using RenzoBackend.Services.Auth;
using RenzoBackend.Services.Background;
using RenzoBackend.Services.Bridge;
using RenzoBackend.Services.Daily;
using RenzoBackend.Services.Downloads;
using RenzoBackend.Services.Helpers;
using RenzoBackend.Services.Images;
using RenzoBackend.Services.Images.Providers;
using RenzoBackend.Services.Import;
using RenzoBackend.Services.Jobs;
using RenzoBackend.Services.Jobs.Settings;
using RenzoBackend.Services.Opds;
using RenzoBackend.Services.Providers;
using RenzoBackend.Services.ReadState;
using RenzoBackend.Services.Scrobbling;
using RenzoBackend.Services.Scrobbling.Abstractions;
using RenzoBackend.Services.Search;
using RenzoBackend.Services.Series;
using RenzoBackend.Services.Settings;
using RenzoBackend.Services.Users;
using Microsoft.Extensions.DependencyInjection.Extensions;
using sun.net.www.http;
using System.Net.Http.Headers;

namespace RenzoBackend.Services
{
    public static class ServiceExtensions
    {
        public static IServiceCollection AddImportService(this IServiceCollection services)
        {
            services.TryAddScoped<SeriesScanner>();
            services.TryAddScoped<SeriesComparer>();
            services.TryAddScoped<ImportQueryService>();
            services.TryAddScoped<ImportCommandService>();
            services.TryAddScoped<UserImportService>();
            return services;
        }

        public static IServiceCollection AddRenzoJsonService(this IServiceCollection services)
        {
            // Singleton: shared per-file lock state across all services
            services.TryAddSingleton<RenzoJsonService>();
            return services;
        }

        public static IServiceCollection AddSeriesServices(this IServiceCollection services)
        {
            // Specialized series services
            services.TryAddScoped<SeriesQueryService>();
            services.TryAddScoped<SeriesCommandService>();
            services.TryAddScoped<SeriesProviderService>();
            services.TryAddScoped<SeriesArchiveService>();
            services.TryAddScoped<SeriesRelocationService>();
            services.TryAddScoped<LockedChapterSupplementService>();
            services.TryAddScoped<VComicsContentService>();
            services.TryAddScoped<SeriesCategoryResolver>();
            services.TryAddScoped<CategoryMaintenanceService>();
            services.TryAddScoped<CadenceCalculationService>();
            
            // Series state sync service - central authority for renzo.json sync
            services.TryAddScoped<SeriesStateService>();
            
            return services;
        }

        public static IServiceCollection AddJobServices(this IServiceCollection services)
        {
            // Core job services
            services.TryAddScoped<JobManagementService>();
            services.TryAddScoped<JobBusinessService>();
            services.TryAddScoped<JobExecutionService>();
            
            // Configuration and supporting services
            services.TryAddSingleton<JobsSettings>();
            services.TryAddScoped<JobHubReportService>();
            
            return services;
        }
        public static IServiceCollection AddHelperServices(this IServiceCollection services)
        {
            services.TryAddScoped<SettingsService>();

            services.AddScoped<IImageProvider, UrlImageProvider>();
            services.AddScoped<IImageProvider, ExtensionsImageProvider>();
            services.AddScoped<IImageProvider, StorageImageProvider>();
            services.TryAddScoped<ThumbCacheService>();
            services.TryAddScoped<CoverHashService>();
            services.TryAddScoped<Reader.ReaderService>();
            services.TryAddSingleton<Reader.StreamImageCache>();
            services.TryAddScoped<Reader.ReaderPreviewService>();
            services.TryAddScoped<IImageFactory, NetVipsImageFactory>();
            services.TryAddScoped<ArchiveHelperService>();
            services.TryAddScoped<DailyService>();
            services.TryAddScoped<Status.StatusEvaluationService>();
            services.TryAddScoped<MihonBridgeService>();
            services.TryAddScoped<MigrationService>();
            services.TryAddScoped<NouisanceFixer20ExtraLarge>();
            return services;
        }
        public static IServiceCollection AddScrobblingServices(this IServiceCollection services, IConfiguration configuration)
        {
            services.TryAddScoped<ScrobblerTokenProtector>();
            services.TryAddScoped<ITokenStorageService, TokenStorageService>();
            services.TryAddScoped<ScrobblerProviderFactory>();
            services.TryAddScoped<TitleMatcher>();
            services.TryAddScoped<ScrobblerSyncService>();
            services.TryAddScoped<SeriesMatchingService>();

            // Register all IScrobblerProvider implementations.
            // NOTE: Must use AddScoped (not TryAddScoped) so each provider is registered.
            // ScrobblerProviderFactory resolves IEnumerable<IScrobblerProvider>.
            // Direct auth: Kitsu and MangaDex use password-based auth directly
            services.AddScoped<Scrobbling.Abstractions.IScrobblerProvider, Scrobbling.Providers.KitsuScrobblerProvider>();
            services.AddScoped<Scrobbling.Abstractions.IScrobblerProvider, Scrobbling.Providers.MangaDexScrobblerProvider>();

            // AniList and MyAnimeList use OAuth2 via the central OAuth proxy for authorization,
            // but call their respective APIs directly for search/tracking operations.
            services.AddScoped<Scrobbling.Abstractions.IScrobblerProvider, Scrobbling.Providers.AniListScrobblerProvider>();
            services.AddScoped<Scrobbling.Abstractions.IScrobblerProvider, Scrobbling.Providers.MyAnimeListScrobblerProvider>();

            // ComicVine still uses direct API key (no OAuth) - Do not support scrobbling, so commented
            //            services.AddScoped<Scrobbling.Abstractions.IScrobblerProvider, Scrobbling.Providers.ComicVineScrobblerProvider>();

            // Register HTTP clients
            services.AddHttpClient("Scrobbler_AniList", SetHttpClientHeaders);
            services.AddHttpClient("Scrobbler_MAL", SetHttpClientHeaders);
            services.AddHttpClient("Scrobbler_Kitsu", SetHttpClientHeaders);
            services.AddHttpClient("Scrobbler_MangaDex", SetHttpClientHeaders);

            return services;
        }
        private static void SetHttpClientHeaders(System.Net.Http.HttpClient client)
        {
            client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Renzo", "1.0"));
        }
        public static IServiceCollection AddBackgroundServices(this IServiceCollection services)
        {
            services.TryAddSingleton<JobQueueHostedService>();
            services.TryAddSingleton<JobScheduledHostedService>();
            services.AddHostedService<DebouncedScrobblerSyncHostedService>();
            return services;
        }


        public static IServiceCollection AddProviderServices(this IServiceCollection services)
        {

            
            // Provider Services (SRP-focused)
            services.TryAddScoped<ProviderManagerService>();
            services.TryAddScoped<ProviderPreferencesService>();
            
            // Provider Cache and Storage
            services.TryAddScoped<ProviderCacheService>();
            
            return services;
        }

        public static IServiceCollection AddSearchServices(this IServiceCollection services)
        {
            // CQRS Search Services
            services.TryAddScoped<SearchQueryService>();
            services.TryAddScoped<SearchCommandService>();
            
            return services;
        }

        public static IServiceCollection AddDownloadServices(this IServiceCollection services)
        {
            // Download CQRS Services
            services.TryAddScoped<DownloadQueryService>();
            services.TryAddScoped<DownloadCommandService>();
            
            return services;
        }
        
        public static IServiceCollection AddMcpServices(this IServiceCollection services)
        {
            services.TryAddScoped<Mcp.McpToolService>();
            services.TryAddScoped<Mcp.Abstractions.IMcpPermissionService, Mcp.McpPermissionService>();
            return services;
        }

        public static IServiceCollection AddAuthServices(this IServiceCollection services)
        {
            services.TryAddScoped<PasswordService>();
            services.TryAddScoped<OpdsPathGenerator>();
            services.TryAddScoped<JwtTokenService>();
            services.TryAddScoped<UserInviteService>();
            // Per-account brute-force lockout state must be shared across requests.
            services.TryAddSingleton<LoginThrottleService>();
            services.TryAddScoped<EmailService>();
            services.TryAddScoped<UserQueryService>();
            services.TryAddScoped<UserCommandService>();
            services.TryAddSingleton<SiteAuth.CookieJarBridge>();
            services.TryAddSingleton<SiteAuth.SiteCredentialProtector>();
            services.TryAddScoped<SiteAuth.CoinSiteRegistry>();
            services.TryAddScoped<SiteAuth.SiteAuthService>();
            return services;
        }

        public static IServiceCollection AddReadStateServices(this IServiceCollection services)
        {
            services.TryAddSingleton<ReadStateCacheService>();
            services.TryAddSingleton<ReadStateChangeNotifier>();
            services.TryAddScoped<ReadStateService>();
            return services;
        }

        public static IServiceCollection AddOpdsServices(this IServiceCollection services)
        {
            services.TryAddSingleton<HashCacheService>();
            services.TryAddSingleton<OpdsExtractionCoordinator>();
            services.TryAddSingleton<ClientCapabilitiesHelper>();
            services.TryAddScoped<OpdsImageService>();
            services.TryAddScoped<OpdsFeedService>();
            return services;
        }

    }
}
