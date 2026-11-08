using System.Reflection;
using System.Text.Json;
using Mihon.ExtensionsBridge.Core.Runtime.Sidecar;

namespace RenzoBackend.Services.SiteAuth;

/// <summary>
/// Signs in on a site's own sign-in form in the extensions' browser, with no
/// site-specific code: open the site, follow its "Sign in" link or button,
/// fill the form, submit, and wait for the form to go away. The browser shares
/// the extensions' cookie store, so the session lands where they read it.
///
/// For sites whose sign-in never takes a plain POST (a Next.js server action,
/// a client-side SDK), and for sites that keep the sign-in in page storage
/// rather than cookies. A captcha on the form ends the attempt: only a person
/// gets past one.
/// </summary>
public static class BrowserSignIn
{
    internal static readonly Lazy<string> Script = new(() =>
    {
        using Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("autobuy.js")!;
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    /// <summary>The steps' index whose result says how the form submit went.</summary>
    public const int SubmitStep = 4;

    internal static string Call(string fn, params object?[] args) =>
        Script.Value + "\n;window." + fn + "(" + string.Join(",", args.Select(a => JsonSerializer.Serialize(a))) + ")";

    internal static SidecarClient.PageScriptStep Ready() =>
        new("document.readyState=='complete'&&!!document.body&&document.body.innerText.length>200", 30_000);

    /// <summary>The sign-in, as page-script steps to run on any page of the site.</summary>
    public static List<SidecarClient.PageScriptStep> Steps(string user, string pass) => new()
    {
        Ready(),
        new("", 3_000),
        new(Call("__rzLogin", "open"), 2_000),
        new(Call("__rzLogin", "form"), 20_000),
        new(Call("__rzLogin", "submit", user, pass), 2_000),
        new(Call("__rzLogin", "done"), 15_000),
        new("", 3_000),
    };

    /// <summary>
    /// Runs the sign-in from <paramref name="url"/>. Returns "done" when the
    /// form was sent and went away, "rejected" when it stayed up (the site
    /// refused the login), or what stopped it: "captcha", "noform", "nouser",
    /// "nosubmit", "none" (no sign-in on the page).
    /// </summary>
    public static async Task<string> RunAsync(SidecarProcessManager sidecar, string url, string user, string pass, CancellationToken token)
    {
        await sidecar.EnsureStartedAsync().ConfigureAwait(false);
        List<string?> r = await sidecar.Client.RunPageScriptAsync(url, null, Steps(user, pass), token).ConfigureAwait(false);
        string? Result(int i) => i < r.Count ? Unwrap(r[i]) : null;
        string? submit = Result(SubmitStep);
        if (submit == null)
            return Result(2) == "none" ? "none" : "noform";
        if (submit != "submitted")
            return submit;
        return Result(SubmitStep + 1) == "done" ? "done" : "rejected";
    }

    /// <summary>evaluateJavascript hands back JSON: a returned string arrives quoted.</summary>
    internal static string? Unwrap(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return null;
        if (raw.StartsWith('"'))
        {
            try { return JsonSerializer.Deserialize<string>(raw); }
            catch (JsonException) { return raw; }
        }
        return raw;
    }
}
