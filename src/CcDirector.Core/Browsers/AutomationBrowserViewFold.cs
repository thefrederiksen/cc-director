using CcDirector.Core.Utilities;

namespace CcDirector.Core.Browsers;

/// <summary>
/// The finished, render-ready view of one automation browser. Every display decision a client would
/// otherwise make - the status label, the status dot color, the subtitle under the name, which single
/// action to offer, the exact attach command - is folded ONCE here (CLAUDE.md rule 7: the client is
/// dumb). The Control API serializes this record verbatim for the CLI, and the Director's own rail and
/// Settings tab render the SAME record in-process, so no two surfaces can disagree about a browser.
/// </summary>
/// <param name="Id">Stable slug id (also the <c>BU_NAME</c>).</param>
/// <param name="Name">Human-facing, user-editable label.</param>
/// <param name="Browser">"Chrome" or "Edge".</param>
/// <param name="Port">The fixed remote-debugging port.</param>
/// <param name="Status">The folded lifecycle state (see <see cref="AutomationBrowserStatus"/>).</param>
/// <param name="StatusLabel">The human status text: "Ready" / "Needs sign-in" / "Stopped".</param>
/// <param name="DotColor">The status dot's color NAME from the one shared palette ("green" /
/// "yellow" / "grey") - surfaces map the name to a brush via their palette wrapper.</param>
/// <param name="Subtitle">The one-line description under the name, e.g. "Chrome - user@x.com".</param>
/// <param name="ActionLabel">The single next action to offer: "Start" / "Sign in" / "Attach".</param>
/// <param name="Account">The signed-in account read from the browser's own profile, or null.</param>
/// <param name="BuName">The <c>BU_NAME</c> browser-harness attaches with.</param>
/// <param name="BuCdpUrl">The <c>BU_CDP_URL</c> browser-harness attaches with.</param>
/// <param name="AttachCommand">The complete one-liner an agent runs to attach the harness.</param>
/// <param name="UserDataDir">The browser's dedicated user-data directory.</param>
/// <param name="CreatedUtc">When the browser was created.</param>
/// <param name="LastSignedInUtc">When the human last confirmed sign-in, or null if never.</param>
public sealed record AutomationBrowserView(
    string Id,
    string Name,
    string Browser,
    int Port,
    AutomationBrowserStatus Status,
    string StatusLabel,
    string DotColor,
    string Subtitle,
    string ActionLabel,
    string? Account,
    string BuName,
    string BuCdpUrl,
    string AttachCommand,
    string UserDataDir,
    DateTime CreatedUtc,
    DateTime? LastSignedInUtc);

/// <summary>
/// Folds an <see cref="AutomationBrowser"/> into its <see cref="AutomationBrowserView"/>, and answers
/// whether browser-harness (the tool that actually drives these browsers) is installed on this machine.
/// The mapping itself is pure and unit-tested (<see cref="Fold"/>); the async entry points add the two
/// live reads (the debug-port probe and the profile's account) on top.
/// </summary>
public static class AutomationBrowserViewFold
{
    /// <summary>Where "Install Browser Harness" sends the user.</summary>
    public const string HarnessInstallUrl = "https://github.com/browser-use/browser-harness/blob/main/install.md";

    /// <summary>The executable browser-harness ships on PATH; its presence IS the install check.</summary>
    public const string HarnessExecutable = "browser-harness";

    /// <summary>True when browser-harness is installed (resolvable on PATH) on this machine.</summary>
    public static bool IsHarnessInstalled() => ExecutableResolver.Resolve(HarnessExecutable) is not null;

    /// <summary>Fold every registered browser on this machine into its render-ready view.</summary>
    public static async Task<IReadOnlyList<AutomationBrowserView>> ListAsync(CancellationToken ct = default)
    {
        var browsers = AutomationBrowserRegistry.Load();
        var views = new List<AutomationBrowserView>(browsers.Count);
        foreach (var browser in browsers)
            views.Add(await FoldAsync(browser, ct).ConfigureAwait(false));
        return views;
    }

    /// <summary>Fold one browser: probe its live status, read its signed-in account, then map.</summary>
    public static async Task<AutomationBrowserView> FoldAsync(AutomationBrowser browser, CancellationToken ct = default)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));

        var status = await AutomationBrowserService.StatusAsync(browser, ct).ConfigureAwait(false);

        // The account is decoration on the subtitle, never load-bearing: an unreadable profile (first
        // run, mid-write Local State) must not take the whole list down, so this read alone is allowed
        // to degrade to "no account shown" - and it logs when it does.
        string? account = null;
        try
        {
            account = AutomationBrowserService.ReadAccount(browser);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AutomationBrowserViewFold] ReadAccount id={browser.Id} failed (non-fatal): {ex.Message}");
        }

        return Fold(browser, status, account);
    }

    /// <summary>
    /// The pure mapping from (browser, live status, account) to the finished view. No I/O, fully
    /// unit-tested - this is where the display rules live and the ONLY place they may live.
    /// </summary>
    public static AutomationBrowserView Fold(AutomationBrowser browser, AutomationBrowserStatus status, string? account)
    {
        if (browser is null) throw new ArgumentNullException(nameof(browser));

        var attach = AutomationBrowserRegistry.AttachInfoFor(browser);
        return new AutomationBrowserView(
            Id: browser.Id,
            Name: browser.Name,
            Browser: browser.Kind.ToString(),
            Port: browser.Port,
            Status: status,
            StatusLabel: StatusLabel(status),
            DotColor: DotColor(status),
            Subtitle: Subtitle(browser.Kind, status, account),
            ActionLabel: ActionLabel(status),
            Account: account,
            BuName: attach.BuName,
            BuCdpUrl: attach.BuCdpUrl,
            AttachCommand: AttachCommand(browser.Id),
            UserDataDir: browser.UserDataDir,
            CreatedUtc: browser.CreatedUtc,
            LastSignedInUtc: browser.LastSignedInUtc);
    }

    /// <summary>The human status text every surface shows verbatim.</summary>
    public static string StatusLabel(AutomationBrowserStatus status) => status switch
    {
        AutomationBrowserStatus.Stopped => "Stopped",
        AutomationBrowserStatus.NeedsSignIn => "Needs sign-in",
        AutomationBrowserStatus.Ready => "Ready",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
    };

    /// <summary>The status dot's color NAME in the one shared session palette: running and signed in is
    /// green, running but never signed in is yellow (attention), not running is grey.</summary>
    public static string DotColor(AutomationBrowserStatus status) => status switch
    {
        AutomationBrowserStatus.Stopped => "grey",
        AutomationBrowserStatus.NeedsSignIn => "yellow",
        AutomationBrowserStatus.Ready => "green",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
    };

    /// <summary>The single next action a surface offers for this browser.</summary>
    public static string ActionLabel(AutomationBrowserStatus status) => status switch
    {
        AutomationBrowserStatus.Stopped => "Start",
        AutomationBrowserStatus.NeedsSignIn => "Sign in",
        AutomationBrowserStatus.Ready => "Attach",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
    };

    /// <summary>The one-line description under the name: the browser kind, then the most useful fact
    /// (the signed-in account when known, otherwise the state in plain words).</summary>
    public static string Subtitle(BrowserKind kind, AutomationBrowserStatus status, string? account)
    {
        if (!string.IsNullOrWhiteSpace(account))
            return $"{kind} - {account}";

        return status switch
        {
            AutomationBrowserStatus.Stopped => $"{kind} - stopped",
            AutomationBrowserStatus.NeedsSignIn => $"{kind} - not signed in yet",
            AutomationBrowserStatus.Ready => $"{kind} - signed in",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown automation browser status"),
        };
    }

    /// <summary>The complete attach one-liner an agent runs. Built on the slug id (never the free-text
    /// name) so the command is always shell-safe.</summary>
    public static string AttachCommand(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("A browser id is required.", nameof(id));
        return $"eval \"$(cc-devthrottle browser attach '{id}')\"";
    }
}
