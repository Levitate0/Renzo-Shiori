using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Text;
using Mihon.ExtensionsBridge.Core.Runtime;
using Mihon.ExtensionsBridge.Core.Services;
using Mihon.ExtensionsBridge.Models.Abstractions;
using Mihon.ExtensionsBridge.Core.Abstractions;

namespace Mihon.ExtensionsBridge.Core.Extensions
{
    public static class Extensions
    {
        public static IServiceCollection AddExtensionsBridge(this IServiceCollection services)
        {
            services.AddSingleton<IWorkingFolderStructure, WorkingFolderStructure>();
            services.AddSingleton<IDex2JarConverter, Dex2JarConverter>();
            services.AddScoped<IRepositoryDownloader, RepositoryDownloader>();
            services.AddHttpClient(nameof(RepositoryDownloader));
            services.AddSingleton<IInternalRepositoryManager, RepositoryManager>();
            services.AddSingleton<IInternalExtensionManager, ExtensionManager>();
            services.AddSingleton<IRepositoryManager, PublicProxyRepositoryManager>();
            services.AddSingleton<IExtensionManager, PublicProxyExtensionManager>();
            services.AddSingleton<IBridgeManager, BridgeManager>();
            services.AddHostedService<BridgeHost>();

            // JVM sidecar (opt-in via RENZO_USE_SIDECAR=1). Registered after BridgeHost so the
            // working folder is initialized before the sidecar starts.
            if (Environment.GetEnvironmentVariable("RENZO_USE_SIDECAR") == "1")
            {
                services.AddSingleton(sp => new Runtime.Sidecar.SidecarProcessManager(
                    new Runtime.Sidecar.SidecarOptions(),
                    sp.GetRequiredService<IWorkingFolderStructure>(),
                    sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>().CreateLogger("Sidecar")));
                services.AddHostedService<Runtime.Sidecar.SidecarHostedService>();
            }
            return services;
        }
    }
}
