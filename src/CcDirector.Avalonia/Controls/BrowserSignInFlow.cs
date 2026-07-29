using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using CcDirector.Core.Browsers;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The one-time human sign-in flow, shared by the rail group and the Settings tab so the wording and
/// the steps cannot drift: bring the browser up with its account page open, wait for the human to say
/// they finished, and only then record the sign-in. The credentials are typed by the HUMAN in the
/// browser window - DevThrottle only opens the page and records the confirmation. This is the honest
/// constraint the whole feature designs around: the one sign-in cannot be automated away.
/// </summary>
internal static class BrowserSignInFlow
{
    /// <summary>Run the flow. Returns true when the human confirmed the sign-in (recorded), false when
    /// they chose "Not yet" (nothing recorded; the browser stays on the sign-in page).</summary>
    public static async Task<bool> RunAsync(Window owner, AutomationBrowserView view)
    {
        if (owner is null) throw new ArgumentNullException(nameof(owner));
        if (view is null) throw new ArgumentNullException(nameof(view));

        FileLog.Write($"[BrowserSignInFlow] RunAsync: id={view.Id}");
        await Task.Run(() => AutomationBrowserService.SignInAsync(view.Id));

        // Say WHAT to sign in to. The old wording said only "sign in", which left the one instruction
        // that matters unanswered: a browser profile is worth exactly what it is signed in to, and a
        // user who signs in to nothing gets a browser their agents cannot do anything useful with.
        // Both routes are named because both are legitimate - a browser account carries everything at
        // once, and per-site logins keep the profile to just the accounts it is meant to reach.
        var accountKind = view.Browser.Equals("Edge", StringComparison.OrdinalIgnoreCase)
            ? "a Microsoft account"
            : "a Google account";

        var dialog = new ConfirmDialog(
            "Sign in once",
            $"A \"{view.Name}\" window just opened on its sign-in page. Sign in by hand in that window - "
            + "DevThrottle never types your credentials.\n\n"
            + $"Sign in to whatever you want this browser to reach: {accountKind} to bring your profile "
            + "across, or just the individual websites you want an agent to use. Whatever is signed in "
            + "here is what your agents can drive - nothing else.\n\n"
            + "The logins are kept in this browser's own profile, apart from your everyday browser, and "
            + "last until the account signs them out.\n\n"
            + "When you are signed in, click Done.",
            confirmLabel: "Done - I am signed in",
            cancelLabel: "Not yet");
        var confirmed = await dialog.ShowDialog<bool?>(owner) == true;

        if (confirmed)
        {
            AutomationBrowserService.MarkSignedIn(view.Id);
            FileLog.Write($"[BrowserSignInFlow] RunAsync: id={view.Id} confirmed signed in");
        }

        return confirmed;
    }
}
