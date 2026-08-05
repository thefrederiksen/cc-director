using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Transcription;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.HostedAi;

/// <summary>
/// The desktop entry point every voice/Wingman/text-to-speech surface uses to gate on hosted-AI
/// readiness (issue #940, epic #937). It wires the shared <see cref="DirectorHostedAiReadiness"/> to
/// the desktop's real plumbing and shows the ONE shared unavailable dialog, so every surface pre-flights
/// and reports a runtime out-of-credits identically - never a per-surface hand-written string.
/// </summary>
public static class DesktopHostedAiGate
{
    /// <summary>
    /// TEST SEAM. When set, <see cref="CheckAsync"/> returns this instead of doing the real check.
    ///
    /// It exists for ONE thing that cannot be proved otherwise: this pre-flight is the await during
    /// which a user can close the Speak dialog, and a recorder built after that close is rooted by its
    /// own NAudio capture thread and never released. A test has to be able to hold the pre-flight open,
    /// close the window, then let it complete. Null in production, where the real check always runs.
    /// </summary>
    internal static Func<CancellationToken, Task<HostedAiState>>? CheckOverrideForTests;

    /// <summary>
    /// Resolve the current hosted-AI state for the configured mode. Since the Included AI mission
    /// (issue #1360) this makes NO network call: the balance pre-flight is retired (a zero balance says
    /// nothing about whether the server will serve an included call), so the check reads the mode
    /// locally and answers Ready - the runtime 402, reported through the same shared dialog, is the
    /// only gate.
    /// </summary>
    public static Task<HostedAiState> CheckAsync(CancellationToken ct = default)
    {
        var testOverride = CheckOverrideForTests;
        if (testOverride is not null) return testOverride(ct);

        var readiness = DirectorHostedAiReadiness.Create(new HostedAiKeyResolver());
        return readiness.CheckAsync(ct);
    }

    /// <summary>
    /// Pre-flight gate: returns true when hosted AI is ready (the caller proceeds). When not ready it
    /// shows the shared unavailable dialog over <paramref name="owner"/> and returns false (the caller
    /// must NOT record / call). Never throws out of the check to the caller.
    /// </summary>
    public static async Task<bool> EnsureReadyAsync(Window owner, CancellationToken ct = default)
    {
        HostedAiState state;
        try
        {
            state = await CheckAsync(ct).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // A failed readiness check must not block the feature (the runtime 402 stays the
            // authoritative gate) - treat an errored pre-flight as "proceed" and let the call decide.
            FileLog.Write($"[DesktopHostedAiGate] EnsureReadyAsync check FAILED (proceeding): {ex.Message}");
            return true;
        }

        if (state == HostedAiState.Ready) return true;
        await ShowAsync(owner, state).ConfigureAwait(true);
        return false;
    }

    /// <summary>
    /// Map a runtime exception to the shared state and show the dialog when it is an out-of-credits /
    /// cap condition (a 402 surfaced as <see cref="InsufficientCreditsException"/>). Returns true when it
    /// handled the exception (the caller then shows nothing else), false when it was an unrelated error
    /// the caller should surface its own way.
    /// </summary>
    public static async Task<bool> TryHandleRuntimeAsync(Window owner, Exception ex)
    {
        if (ex is InsufficientCreditsException credits)
        {
            var state = HostedAiErrorMapper.MapCode(credits.Code);
            FileLog.Write($"[DesktopHostedAiGate] runtime out-of-credits mapped to {state}");
            await ShowAsync(owner, state).ConfigureAwait(true);
            return true;
        }
        return false;
    }

    /// <summary>Show the one shared unavailable dialog for a state over <paramref name="owner"/>.</summary>
    public static async Task ShowAsync(Window owner, HostedAiState state)
    {
        if (owner is null)
        {
            FileLog.Write($"[DesktopHostedAiGate] ShowAsync: no owner window for state={state}; nothing shown");
            return;
        }
        await new HostedAiUnavailableDialog(state).ShowDialog(owner).ConfigureAwait(true);
    }
}
