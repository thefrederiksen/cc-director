namespace CcDirector.Setup.Cli;

/// <summary>
/// Pure pre-flight decision for a managed Gateway install/update pass. Factored out of
/// <see cref="Commands"/> so the rule can be unit-tested without touching the environment.
///
/// The ONLY hard requirement is the platform: the managed Gateway is a Windows-only tray app. There
/// is deliberately NO OPENAI_API_KEY requirement - there is no bring-your-own-key anywhere; inference
/// routes through DevThrottle's account-minted <c>dt_live_</c> key, which the managed Gateway runtime
/// auto-mints and stores itself after account sign-in. Demanding an OpenAI key here would block a
/// Gateway refresh on machines that never needed one.
/// </summary>
public static class GatewayInstallPreflight
{
    /// <summary>
    /// Returns an error message to print, or null if the pass may proceed. Takes the platform as a
    /// parameter (not read from the environment) so the decision is pure and testable.
    /// </summary>
    public static string? Check(bool isWindows)
    {
        if (!isWindows)
            return "ERROR: The Gateway role is Windows-only.";
        return null;
    }
}
