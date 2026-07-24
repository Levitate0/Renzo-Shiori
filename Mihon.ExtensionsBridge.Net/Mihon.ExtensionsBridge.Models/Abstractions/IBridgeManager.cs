namespace Mihon.ExtensionsBridge.Models.Abstractions;

public interface IBridgeManager
{
    public bool Initialized { get; }
    IExtensionManager LocalExtensionManager { get; }
    IRepositoryManager OnlineRepositoryManager { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    void Shutdown();
    Task<Preferences> GetPreferencesAsync(CancellationToken cancellationToken);
    Task SetPreferencesAsync(Models.Preferences prefs, CancellationToken cancellationToken);

    /// <summary>
    /// Adjusts the shared OkHttp client's concurrency limits at runtime, in place
    /// (no rebuild/restart). <paramref name="maxRequestsPerHost"/> is clamped to
    /// 5–12 and caps how many requests hit a single host at once — the real
    /// ceiling on per-source download throughput. Higher = faster on big backlogs
    /// but harder on the host (rate-limit/ban risk).
    /// </summary>
    void SetMaxRequestsPerHost(int maxRequestsPerHost);
}
