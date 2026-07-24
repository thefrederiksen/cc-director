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

        var dialog = new ConfirmDialog(
            "Sign in once",
            $"A \"{view.Name}\" window just opened on its sign-in page. Sign in by hand in that window - " +
            "DevThrottle never types your credentials. The login is saved in this browser's own profile " +
            "and lasts until the account signs it out.\n\nWhen you are signed in, click Done.",
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
