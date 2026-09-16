namespace Mihon.ExtensionsBridge.Core.Runtime.Sidecar
{
    /// <summary>
    /// Translates the app's <see cref="Mihon.ExtensionsBridge.Models.Preferences"/> into the flat key/value map the
    /// sidecar's <c>/config</c> endpoint understands.
    ///
    /// This lives in one place on purpose. The map is posted from TWO call sites —
    /// <c>BridgeManager.SetPreferencesAsync</c> when the user saves settings, and
    /// <c>SidecarProcessManager.EnsureStartedAsync</c> on every (re)start — and the
    /// two drifting apart is exactly the bug this file was written to close: the
    /// startup path did not push settings at all, so a freshly started JVM fell back
    /// to the Kotlin defaults in <c>SettingsConfig.Settings</c>, where
    /// <c>flareSolverrEnabled</c> is FALSE. Every watchdog recycle silently turned the
    /// Cloudflare bypass off again and the only thing that turned it back on was a
    /// user saving the settings page.
    ///
    /// Values are strings because the sidecar parses them out of a JSON object with
    /// typesafe-config, which accepts the string forms and not .NET's <c>True</c>.
    /// </summary>
    public static class SidecarNetworkSettings
    {
        /// <summary>Same fallback the settings page offers when no URL is configured.</summary>
        public const string DefaultFlareSolverrUrl = "http://127.0.0.1:8189";

        public static Dictionary<string, object?> Build(Mihon.ExtensionsBridge.Models.Preferences? prefs)
        {
            var settings = new Dictionary<string, object?>();
            if (prefs == null)
                return settings;

            if (prefs.FlareSolverr != null)
            {
                settings["flareSolverrEnabled"] = prefs.FlareSolverr.Enabled.ToString().ToLowerInvariant();
                settings["flareSolverrUrl"] = prefs.FlareSolverr.Url ?? DefaultFlareSolverrUrl;
                settings["flareSolverrTimeout"] = prefs.FlareSolverr.Timeout.ToString();
                settings["flareSolverrSessionName"] = prefs.FlareSolverr.SessionName;
                settings["flareSolverrSessionTtl"] = prefs.FlareSolverr.SessionTtl.ToString();
                settings["flareSolverrAsResponseFallback"] = prefs.FlareSolverr.AsResponseFallback.ToString().ToLowerInvariant();
            }

            if (prefs.SocksProxy != null)
            {
                settings["socksProxyEnabled"] = prefs.SocksProxy.Enabled.ToString().ToLowerInvariant();
                settings["socksProxyHost"] = prefs.SocksProxy.Host ?? "";
                settings["socksProxyPort"] = prefs.SocksProxy.Port == 0 ? "" : prefs.SocksProxy.Port.ToString();
                settings["socksProxyUsername"] = prefs.SocksProxy.Username;
                settings["socksProxyPassword"] = prefs.SocksProxy.Password;
            }

            return settings;
        }
    }
}
