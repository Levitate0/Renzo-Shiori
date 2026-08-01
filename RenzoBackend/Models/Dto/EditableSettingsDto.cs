using System.Text.Json.Serialization;

namespace RenzoBackend.Models.Dto;

public class EditableSettingsDto
{
    [JsonPropertyName("preferredLanguages")]
    public string[] PreferredLanguages { get; set; } = [];
    [JsonPropertyName("mihonRepositories")]
    public string[] MihonRepositories { get; set; } = [];
    [JsonPropertyName("numberOfSimultaneousDownloads")]
    public int NumberOfSimultaneousDownloads { get; set; } = 10;

    [JsonPropertyName("numberOfSimultaneousSearches")]
    public int NumberOfSimultaneousSearches { get; set; } = 10;
    [JsonPropertyName("chapterDownloadFailRetryTime")]
    public TimeSpan ChapterDownloadFailRetryTime { get; set; } = TimeSpan.FromMinutes(30);
    [JsonPropertyName("chapterDownloadFailRetries")]
    public int ChapterDownloadFailRetries { get; set; } = 144;

    [JsonPropertyName("perTitleUpdateSchedule")]
    public TimeSpan PerTitleUpdateSchedule { get; set; }
    [JsonPropertyName("perSourceUpdateSchedule")]
    public TimeSpan PerSourceUpdateSchedule { get; set; }
    [JsonPropertyName("extensionsCheckForUpdateSchedule")]
    public TimeSpan ExtensionsCheckForUpdateSchedule { get; set; }

    [JsonPropertyName("categorizedFolders")]
    public bool CategorizedFolders { get; set; } = true;
    [JsonPropertyName("categories")]
    public string[] Categories { get; set; } = [];
    [JsonPropertyName("flareSolverrEnabled")]
    public bool FlareSolverrEnabled { get; set; }
    [JsonPropertyName("flareSolverrUrl")]
    public string FlareSolverrUrl { get; set; } = "http://localhost:8191";
    [JsonPropertyName("flareSolverrTimeout")]
    public TimeSpan FlareSolverrTimeout { get; set; } = TimeSpan.FromSeconds(60);
    [JsonPropertyName("flareSolverrSessionTtl")]
    public TimeSpan FlareSolverrSessionTtl { get; set; } = TimeSpan.FromMinutes(15);
    [JsonPropertyName("flareSolverrAsResponseFallback")]
    public bool FlareSolverrAsResponseFallback { get; set; } = false;

    [JsonPropertyName("isWizardSetupComplete")]
    public bool IsWizardSetupComplete { get; set; } = false;

    [JsonPropertyName("wizardSetupStepCompleted")]
    public int WizardSetupStepCompleted { get; set; } = 0;

    [JsonPropertyName("numberOfSimultaneousDownloadsPerProvider")]
    public int NumberOfSimultaneousDownloadsPerProvider { get; set; } = 3;

    /// <summary>
    /// How many page images are fetched concurrently within a single chapter.
    /// Pages are still written to the archive in order. 1 = the old strictly
    /// serial behavior; higher is much faster on high-latency sources but hits
    /// them harder, so sources that rate-limit may prefer a low value.
    /// </summary>
    [JsonPropertyName("pagesInParallelPerChapter")]
    public int PagesInParallelPerChapter { get; set; } = 5;

    /// <summary>
    /// Ceiling (MB) on page-image data held in memory at once across ALL
    /// concurrent downloads. Fetches wait for a slot rather than piling up, so
    /// aggressive concurrency throttles itself instead of exhausting RAM.
    /// </summary>
    [JsonPropertyName("downloadMemoryBudgetMB")]
    public int DownloadMemoryBudgetMB { get; set; } = 2048;

    /// <summary>
    /// How many HTTP requests may run concurrently against a SINGLE host
    /// (OkHttp's maxRequestsPerHost). This is the real ceiling on per-source
    /// download speed: all page fetches for one site share this budget, no matter
    /// how many chapters run. 5 is OkHttp's default. Raising it speeds up sources
    /// you have a large backlog on, but hammers that one host harder — values near
    /// the top of the range make rate-limiting or an IP ban materially more likely
    /// on strict sources. Clamped to 5–12.
    /// </summary>
    [JsonPropertyName("maxRequestsPerHost")]
    public int MaxRequestsPerHost { get; set; } = 5;

    [JsonPropertyName("socksProxyEnabled")]
    public bool SocksProxyEnabled { get; set; } = false;
    [JsonPropertyName("socksProxyVersion")]
    public int SocksProxyVersion { get; set; } = 5;
    [JsonPropertyName("socksProxyHost")]
    public string SocksProxyHost { get; set; } = "";
    [JsonPropertyName("socksProxyPort")]
    public int SocksProxyPort { get; set; } = 0;
    [JsonPropertyName("socksProxyUsername")]
    public string SocksProxyUsername { get; set; } = "";
    [JsonPropertyName("socksProxyPassword")]
    public string SocksProxyPassword { get; set; } = "";
    [JsonPropertyName("nsfwVisibility")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public NsfwVisibility NsfwVisibility { get; set; } = NsfwVisibility.HideByDefault;

    // --- Health Monitoring Thresholds ---

    [JsonPropertyName("releaseCadenceMultiplierYellow")]
    public double ReleaseCadenceMultiplierYellow { get; set; } = 2.0;

    [JsonPropertyName("releaseCadenceMultiplierRed")]
    public double ReleaseCadenceMultiplierRed { get; set; } = 5.0;

    [JsonPropertyName("releaseCadenceDefaultDays")]
    public int ReleaseCadenceDefaultDays { get; set; } = 7;

    [JsonPropertyName("providerErrorYellowHours")]
    public int ProviderErrorYellowHours { get; set; } = 48;

    [JsonPropertyName("providerErrorRedHours")]
    public int ProviderErrorRedHours { get; set; } = 168;

    // --- Authentication Settings ---

    [JsonPropertyName("authenticationEnabled")]
    public bool AuthenticationEnabled { get; set; } = false;

    [JsonPropertyName("externalDomain")]
    public string ExternalDomain { get; set; } = string.Empty;

    /// <summary>
    /// CORS origin allowlist. Empty = permissive AllowAnyOrigin (without
    /// credentials) for installs that never configured it; non-empty = strict
    /// allowlist with credentials. Applied live via DynamicCorsPolicyProvider.
    /// </summary>
    [JsonPropertyName("allowedOrigins")]
    public string[] AllowedOrigins { get; set; } = [];

    [JsonPropertyName("sessionExpirationHours")]
    public int SessionExpirationHours { get; set; } = 24;

    [JsonPropertyName("rememberMeExpirationDays")]
    public int RememberMeExpirationDays { get; set; } = 90;

    // --- Email (SMTP) Settings ---
    // Outbound submission to an external relay (Gmail, Brevo, …); used for
    // self-service password-reset emails. Empty host = email features off.

    [JsonPropertyName("smtpHost")]
    public string SmtpHost { get; set; } = string.Empty;

    [JsonPropertyName("smtpPort")]
    public int SmtpPort { get; set; } = 587;

    [JsonPropertyName("smtpUsername")]
    public string SmtpUsername { get; set; } = string.Empty;

    [JsonPropertyName("smtpPassword")]
    public string SmtpPassword { get; set; } = string.Empty;

    [JsonPropertyName("smtpUseSsl")]
    public bool SmtpUseSsl { get; set; } = true;

    [JsonPropertyName("smtpFromAddress")]
    public string SmtpFromAddress { get; set; } = string.Empty;

    // --- Reader ---

    /// <summary>Enables the built-in web reader (library reading + Browse preview).</summary>
    [JsonPropertyName("readerEnabled")]
    public bool ReaderEnabled { get; set; } = true;

    // --- Downloads ---

    /// <summary>
    /// Download EVERY chapter, ignoring each series' start-point cutoff. When on,
    /// the update scan queues all missing chapters (including gaps below the latest
    /// downloaded), so the whole library gets pulled — not just new chapters.
    /// </summary>
    [JsonPropertyName("downloadAllChapters")]
    public bool DownloadAllChapters { get; set; } = false;

    /// <summary>
    /// Rolling library scan interval in hours (clamped 3–12): how often the whole
    /// library is checked for new chapters, independent of per-title schedules.
    /// </summary>
    [JsonPropertyName("libraryScanIntervalHours")]
    public int LibraryScanIntervalHours { get; set; } = 6;

    // Source-priority upgrades (re-download from a newly-higher-priority source)
    // and the default source-priority order are PER-USER, not instance-wide —
    // see RenzoBackend/Extensions/UserPriorityPrefsExtensions.cs (stored in each
    // user's own Preferences JSON blob, same place their theme lives). They used
    // to live here as instance-wide settings; moved 2026-07-30.
}
public enum NsfwVisibility
{
    AlwaysHide = 0,
    HideByDefault = 1,
    Show = 2,
}