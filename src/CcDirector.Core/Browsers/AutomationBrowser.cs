using System.Text.Json.Serialization;

namespace CcDirector.Core.Browsers;

/// <summary>
/// A DevThrottle-owned, agent-drivable browser. Unlike a real personal profile (which Chrome 136+
/// refuses to expose over remote-debugging, and whose logged-in session App-Bound Encryption refuses
/// to copy), an <see cref="AutomationBrowser"/> is a SEPARATE Chromium user-data directory with its
/// OWN fixed remote-debugging port. It is signed in ONCE by the human and thereafter driven hands-off
/// by an agent through the browser-harness / Chrome DevTools Protocol.
///
/// Machine-locality is a real property, not a caveat: the directory and the loopback debug port only
/// mean anything on the machine they live on, so a browser is owned by the LOCAL Director and is only
/// drivable by an agent on that same machine (mirroring how repositories and sessions are per-machine).
/// </summary>
/// <param name="Id">Stable slug derived from the name at creation (e.g. "center-consulting"). Used as
/// the <c>BU_NAME</c> the harness attaches with and as the on-disk sub-directory name. Never changes,
/// even when <paramref name="Name"/> is edited.</param>
/// <param name="Name">Human-facing, user-editable label (e.g. "Center Consulting").</param>
/// <param name="Kind">Which Chromium browser this drives (Chrome or Edge).</param>
/// <param name="UserDataDir">This browser's dedicated <c>--user-data-dir</c> (absolute path). Never a
/// real personal profile - always a folder DevThrottle created under <see cref="Storage.CcStorage.Browsers"/>.</param>
/// <param name="Port">The fixed <c>--remote-debugging-port</c> this browser always launches on, so the
/// attach environment (<c>BU_CDP_URL</c>) is stable across restarts.</param>
/// <param name="CreatedUtc">When the browser was created.</param>
/// <param name="LastSignedInUtc">When the human last confirmed they signed this browser in, or null if
/// it has never been signed in. Null is what makes <see cref="AutomationBrowserStatus.NeedsSignIn"/>
/// distinguishable from <see cref="AutomationBrowserStatus.Ready"/>.</param>
public sealed record AutomationBrowser(
    string Id,
    string Name,
    BrowserKind Kind,
    string UserDataDir,
    int Port,
    DateTime CreatedUtc,
    DateTime? LastSignedInUtc);

/// <summary>
/// The lifecycle state of an <see cref="AutomationBrowser"/>, folded ONCE here so every reader (CLI
/// table, and later the rail UI) renders the same verdict verbatim rather than re-deriving it.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AutomationBrowserStatus
{
    /// <summary>The browser's debug port is not answering - the browser is not running.</summary>
    Stopped,

    /// <summary>The browser is running but has never been signed in (<see cref="AutomationBrowser.LastSignedInUtc"/> is null).
    /// An agent can attach, but pages behind a login will bounce to a sign-in screen.</summary>
    NeedsSignIn,

    /// <summary>The browser is running AND has been signed in - fully drivable by an agent.</summary>
    Ready,
}

/// <summary>
/// The two environment variables browser-harness reads to attach to a specific browser. Returned by
/// <see cref="AutomationBrowserService.AttachInfo"/> and printed by <c>cc-devthrottle browser attach</c>
/// as shell <c>export</c> lines, so <c>eval "$(cc-devthrottle browser attach 'Name')"</c> points the
/// harness at the right browser. This REPLACES the personal ad-hoc <c>env cencon</c> command.
/// </summary>
/// <param name="BuName">The value for <c>BU_NAME</c> - the browser's <see cref="AutomationBrowser.Id"/>.</param>
/// <param name="BuCdpUrl">The value for <c>BU_CDP_URL</c> - <c>http://127.0.0.1:&lt;port&gt;</c>.</param>
public sealed record AttachInfo(string BuName, string BuCdpUrl);
