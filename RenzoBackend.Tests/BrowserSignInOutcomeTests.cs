using RenzoBackend.Services.SiteAuth;
using Xunit;

namespace RenzoBackend.Tests;

/// <summary>
/// The auto-buy log used to say only "browser sign-in: submitted", which hid
/// whether the site took the login (EZmanga, 2026-11-17: two failed buys with
/// no way to tell). The outcome now says which, and what the site showed.
/// </summary>
public class BrowserSignInOutcomeTests
{
    private static Func<int, string?> Steps(string? submit, string? done = null, string? said = null) => i =>
        i == BrowserSignIn.SubmitStep ? submit
        : i == BrowserSignIn.SubmitStep + 1 ? done
        : i == BrowserSignIn.SaidStep ? said
        : null;

    [Fact]
    public void Form_gone_is_signed_in() =>
        Assert.Equal("signed in", BrowserSignIn.Describe(Steps("submitted", "done", "-")));

    [Fact]
    public void Form_still_up_is_refused_with_the_sites_words() =>
        Assert.Equal("refused, site said \"Too many login attempts\"",
            BrowserSignIn.Describe(Steps("submitted", null, "Too many login attempts")));

    [Fact]
    public void Stopped_before_submitting_keeps_the_reason()
    {
        Assert.Equal("captcha", BrowserSignIn.Describe(Steps("captcha")));
        Assert.Equal("no form", BrowserSignIn.Describe(Steps(null)));
    }

    [Fact]
    public void Said_step_comes_after_done_and_before_the_final_wait()
    {
        var steps = BrowserSignIn.Steps("u", "p");
        Assert.Contains("\"done\"", steps[BrowserSignIn.SubmitStep + 1].Js);
        Assert.Contains("\"said\"", steps[BrowserSignIn.SaidStep].Js);
        Assert.Equal(BrowserSignIn.SaidStep + 2, steps.Count);
    }
}
