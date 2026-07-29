namespace CcDirector.Setup.Cli;

/// <summary>
/// The command line installer's exit codes - a CONTRACT, not an implementation detail.
///
/// This is the unattended install path, so its callers are scripts and coding agents rather than
/// people. An agent has to distinguish "installed", "there was nothing to do", "a component failed"
/// and "a human is needed" without reading prose, and the numbers were previously duplicated as
/// private constants in two files with nothing pinning them. Anything that changes here changes the
/// behaviour of every script and every agent prompt that branches on it.
///
/// Documented for agents in docs/public/getting-started/02-installation.md and pinned by
/// ExitCodeContractTests.
/// </summary>
public static class ExitCodes
{
    /// <summary>The command did what was asked. For <c>install</c> and <c>update</c> this includes
    /// "everything was already current" - nothing to do is a success, not a special case.</summary>
    public const int Ok = 0;

    /// <summary>The command ran and failed: a component did not install, a download or hash check
    /// failed, or an operation the tool owns did not complete. The output names what failed.</summary>
    public const int Error = 1;

    /// <summary>The command line itself was wrong - unknown verb, missing value, bad combination.
    /// Distinct from <see cref="Error"/> so a caller can tell "I asked wrongly" from "it went
    /// wrong", and never retry the former.</summary>
    public const int Usage = 2;

    /// <summary>Nothing is missing that this tool can install, but the machine is not useful yet:
    /// there is no coding agent on it. Reported by <c>prereqs</c>. An agent should surface this to a
    /// person rather than retrying, because no amount of retrying installs an agent.</summary>
    public const int PrerequisiteMissing = 3;
}
