using System.Net.Http.Headers;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Update;

/// <summary>What happened when the Director asked its launcher to restart it.</summary>
/// <param name="Ok">True when the launcher accepted the request.</param>
/// <param name="Message">One plain sentence, suitable for showing to a person either way.</param>
public sealed record LauncherRestartResult(bool Ok, string Message);

/// <summary>
/// Asks the local launcher to stop this Director, install whatever is staged, and start it again -
/// the "install it now" action behind the update status (issue #1030).
///
/// It goes THROUGH the launcher rather than restarting from inside the Director, for the reason the
/// whole update moved to the launcher in the first place (issue #1033): whatever replaces the binary
/// has to outlive the process being replaced, so a Director that restarted itself would leave nothing
/// behind able to confirm the new build came up, or to put the old one back when it did not. The
/// launcher is still there afterwards. This action is therefore not a shortcut around the launcher's
/// ownership; it is a request to do now what it was going to do on its next pass anyway.
///
/// The action is only ever OFFERED when the fold says it can be (a build is staged, no session would
/// be interrupted, and a launcher is reachable), so a failure here means the machine changed under the
/// person between the offer and the click. That is reported as itself, never swallowed.
/// </summary>
public static class LauncherRestartClient
{
    /// <summary>
    /// Where the launcher keeps the bearer token for its own REST interface. Defined here, once,
    /// because both the launcher that writes it and the Director that reads it need the same answer.
    /// </summary>
    public static string TokenFile { get; } =
        Path.Combine(CcStorage.ToolConfig("launcher"), "launcher-token.txt");

    /// <summary>
    /// Ask the launcher to restart the Director. Never throws: this is behind a button, so every
    /// failure comes back as a sentence the caller can show.
    /// </summary>
    public static async Task<LauncherRestartResult> RequestRestartAsync(
        HttpMessageHandler? handler = null, CancellationToken ct = default)
    {
        FileLog.Write("[LauncherRestartClient] RequestRestartAsync");
        try
        {
            var launcher = LauncherDiscovery.Read();
            if (launcher.Port is not { } port)
            {
                var why = launcher.Error ?? (launcher.Installed
                    ? "the launcher did not record a port"
                    : "no launcher is running on this machine");
                FileLog.Write($"[LauncherRestartClient] cannot reach a launcher: {why}");
                return new LauncherRestartResult(false,
                    $"Could not ask the launcher to restart the Director: {why}. Closing and reopening the "
                    + "Director installs the update instead.");
            }

            if (!File.Exists(TokenFile))
            {
                FileLog.Write($"[LauncherRestartClient] no launcher token at {TokenFile}");
                return new LauncherRestartResult(false,
                    "Could not ask the launcher to restart the Director: its access token is missing. Closing and "
                    + "reopening the Director installs the update instead.");
            }

            var token = (await File.ReadAllTextAsync(TokenFile, ct)).Trim();

            using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
            // The launcher stops the Director, swaps the build, starts it and waits for the new version to
            // answer. That is minutes on a cold machine, and a timeout here would report a failure for an
            // install that was going perfectly well.
            http.Timeout = TimeSpan.FromMinutes(5);
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await http.PostAsync($"http://127.0.0.1:{port}/director/restart", content: null, ct);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(ct);
                FileLog.Write($"[LauncherRestartClient] launcher answered {(int)response.StatusCode}: {body}");
                return new LauncherRestartResult(false,
                    $"The launcher refused the restart ({(int)response.StatusCode}). Closing and reopening the "
                    + "Director installs the update instead.");
            }

            FileLog.Write("[LauncherRestartClient] the launcher accepted the restart request");
            return new LauncherRestartResult(true, "Installing the update now - the Director will restart.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LauncherRestartClient] RequestRestartAsync FAILED: {ex.Message}");
            return new LauncherRestartResult(false,
                $"Could not ask the launcher to restart the Director: {ex.Message}. Closing and reopening the "
                + "Director installs the update instead.");
        }
    }
}
