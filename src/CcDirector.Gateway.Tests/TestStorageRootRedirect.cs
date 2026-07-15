using System.Runtime.CompilerServices;
using CcDirector.Core.Storage;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Redirects <see cref="CcStorage.Root"/> to a throwaway temporary directory for the WHOLE test assembly,
/// before any test runs.
///
/// WHAT THIS GUARDS, AND IT IS NOT THE STATISTICS. Forty-eight test files in this assembly construct a
/// <c>GatewayHost</c>. Its constructor points live stores at <c>CcStorage.Root()</c> when it is given no
/// explicit path - and that root is the OWNER'S REAL <c>%LOCALAPPDATA%\cc-director</c>, holding his live
/// fleet state: <c>missions.json</c> (the running missions, including the one that produced this file),
/// <c>cronjobs.json</c>, <c>keyvault.json</c>, and the statistics stores. Without this redirect, running the
/// test suite writes into all of it.
///
/// This is not theoretical. On 2026-07-15 a full-suite run drove the then-current input-statistics import
/// against the owner's LIVE gateway-input-stats.json and RENAMED IT ASIDE. Nothing was lost, but only by
/// luck: the running Gateway happened to hold its state in memory and rewrote the file on its next save. A
/// Gateway restart inside that window would have found no file, started empty, and saved empty over it -
/// losing the lot. The exposure had existed for a long time; the import was merely the first operation that
/// MOVED a file rather than overwriting it with equivalent content.
///
/// WHY A MODULE INITIALIZER RATHER THAN A CONVENTION. The hazard was already known and already mitigated -
/// by a convention. Twenty-two of the forty-eight files carefully set and restore CC_DIRECTOR_ROOT
/// themselves. The other twenty-six do not. A convention followed by fewer than half the sites that need it
/// is not a mitigation, it is a folk memory: it was demonstrably forgotten twenty-six times, by people who
/// knew about it. So the protection belongs somewhere it cannot be forgotten - here, once, for everyone.
///
/// <see cref="CcStorage.Root"/> re-reads the environment variable on EVERY call (CcStorage.cs:32-34), so a
/// process-wide redirect also protects the stores that accept no path at all and resolve the root
/// themselves, such as <c>GatewaySessionConcurrencyStats</c>.
///
/// Pinned by <see cref="TestStorageRootRedirectTests"/>, which asserts the redirect is actually in force. An
/// initializer that silently fails to run is exactly the dead-test-that-looks-like-coverage shape, and this
/// one is guarding the owner's live state.
/// </summary>
internal static class TestStorageRootRedirect
{
    /// <summary>The temporary root this assembly's tests resolve to.</summary>
    internal static string Root { get; private set; } = "";

    [ModuleInitializer]
    internal static void Redirect()
    {
        // Unconditional, deliberately. Respecting an already-set value would look considerate and would be
        // a hole: if the variable happened to be pointing at the real root, the suite would quietly run
        // unprotected, which is the failure this file exists to end. A test needing a specific root sets it
        // for itself - twenty-two of them already do, and they still work, because they set it after this
        // has run and restore it to this temporary root afterwards.
        Root = Path.Combine(Path.GetTempPath(), "cc-gateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", Root);

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(Root, recursive: true); } catch (Exception) { /* best effort */ }
        };
    }
}
