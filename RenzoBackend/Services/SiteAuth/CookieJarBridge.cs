using java.net;

namespace RenzoBackend.Services.SiteAuth;

/// <summary>
/// One harvested cookie ready to inject: name, value, and the domain/path it
/// scopes to.
/// </summary>
public record HarvestedCookie(string Name, string Value, string Domain, string Path = "/", bool Secure = true);

/// <summary>
/// Reaches the LIVE cookie jar the Mihon extensions share and injects site
/// login cookies into it, in-process, with no bridge rebuild or restart.
///
/// The bridge registers its PersistentCookieStore as the JVM's global default
/// cookie handler (NetworkHelper: CookieHandler.setDefault(CookieManager(store,
/// ACCEPT_ALL))), and PersistentCookieStore is a standard java.net.CookieStore
/// that persists every add() to cookie_store.xml. So from C# (via IKVM) we get
/// that same store through the JDK API and add() into it — every extension's
/// OkHttp client then sends those cookies, and the source serves owned chapters.
/// </summary>
public class CookieJarBridge
{
    private readonly ILogger _logger;

    public CookieJarBridge(ILogger<CookieJarBridge> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// The extensions' shared cookie store, or null if the bridge hasn't
    /// initialized its default cookie handler yet (should only happen very early
    /// in startup, before any source has been used).
    /// </summary>
    private CookieStore? Store
    {
        get
        {
            try
            {
                CookieHandler? handler = CookieHandler.getDefault();
                return (handler as CookieManager)?.getCookieStore();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not reach the shared cookie store");
                return null;
            }
        }
    }

    /// <summary>True when the live jar is reachable.</summary>
    public bool IsAvailable => Store != null;

    /// <summary>
    /// Injects cookies into the live jar for a site. Returns the number added.
    /// A cookie with the same name+domain replaces the old one (JDK CookieStore
    /// semantics), so re-login refreshes an expiring session in place.
    /// </summary>
    public int Inject(IEnumerable<HarvestedCookie> cookies)
    {
        CookieStore? store = Store;
        if (store == null)
            return 0;

        int added = 0;
        foreach (HarvestedCookie c in cookies)
        {
            try
            {
                var cookie = new HttpCookie(c.Name, c.Value);
                cookie.setDomain(c.Domain);
                cookie.setPath(string.IsNullOrEmpty(c.Path) ? "/" : c.Path);
                cookie.setSecure(c.Secure);
                cookie.setVersion(0);
                // A leading-dot domain (".example.com") matches subdomains too,
                // which is what session cookies for these sites expect.
                string host = c.Domain.TrimStart('.');
                store.add(new URI("https://" + host + "/"), cookie);
                added++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to inject cookie {Name} for {Domain}", c.Name, c.Domain);
            }
        }
        if (added > 0)
            _logger.LogInformation("Injected {Count} cookies into the shared jar", added);
        return added;
    }

    /// <summary>
    /// Snapshots the cookies currently in the jar for a host (leading-dot and
    /// bare-host both checked), so a successful login's cookies can be persisted
    /// and re-injected after a restart.
    /// </summary>
    public List<HarvestedCookie> Snapshot(string host)
    {
        CookieStore? store = Store;
        var result = new List<HarvestedCookie>();
        if (store == null)
            return result;

        host = host.TrimStart('.');
        try
        {
            var uris = new[] { new URI("https://" + host + "/") };
            foreach (URI uri in uris)
            {
                java.util.List cookies = store.get(uri);
                for (int i = 0; i < cookies.size(); i++)
                {
                    if (cookies.get(i) is HttpCookie hc)
                    {
                        result.Add(new HarvestedCookie(
                            hc.getName(), hc.getValue(),
                            string.IsNullOrEmpty(hc.getDomain()) ? host : hc.getDomain(),
                            string.IsNullOrEmpty(hc.getPath()) ? "/" : hc.getPath(),
                            hc.getSecure()));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not snapshot cookies for {Host}", host);
        }
        return result;
    }

    /// <summary>Removes every cookie for a host (used on "log out"/delete).</summary>
    public void ClearHost(string host)
    {
        CookieStore? store = Store;
        if (store == null)
            return;
        host = host.TrimStart('.');
        try
        {
            java.util.List all = store.getCookies();
            for (int i = 0; i < all.size(); i++)
            {
                if (all.get(i) is HttpCookie hc)
                {
                    string d = (hc.getDomain() ?? "").TrimStart('.');
                    if (d.Equals(host, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
                    {
                        try { store.remove(new URI("https://" + d + "/"), hc); } catch { /* best-effort */ }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not clear cookies for {Host}", host);
        }
    }
}
