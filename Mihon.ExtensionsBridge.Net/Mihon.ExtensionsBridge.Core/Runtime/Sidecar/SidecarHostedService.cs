using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>Boots the JVM sidecar on startup when it's enabled, and tears it down on shutdown.</summary>
    public sealed class SidecarHostedService : IHostedService
    {
        private readonly SidecarProcessManager _mgr;
        private readonly ILogger<SidecarHostedService> _logger;

        public SidecarHostedService(SidecarProcessManager mgr, ILogger<SidecarHostedService> logger)
        {
            _mgr = mgr;
            _logger = logger;
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _mgr.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Don't crash the app if the sidecar can't start; the IKVM path remains a fallback.
                _logger.LogError(ex, "JVM sidecar failed to start; extension loading will fall back to IKVM.");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken) => _mgr.DisposeAsync().AsTask();
    }
}
