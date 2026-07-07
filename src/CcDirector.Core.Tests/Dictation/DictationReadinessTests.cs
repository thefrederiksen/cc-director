using CcDirector.Core.Dictation;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// The readiness gate that stops a dictation from being typed into a busy, streaming composer
/// (issue #1135). A dictation may only land when the session is idle at its prompt; every other
/// activity state defers.
/// </summary>
public sealed class DictationReadinessTests
{
    [Theory]
    [InlineData(ActivityState.WaitingForInput, true)]  // at the prompt, needs the user - safe to type
    [InlineData(ActivityState.Idle, true)]             // ready at the prompt
    [InlineData(ActivityState.Working, false)]         // streaming output - the state that piled up copies
    [InlineData(ActivityState.WaitingForPerm, false)]  // a permission prompt a free-text line would answer wrongly
    [InlineData(ActivityState.Starting, false)]        // composer still initializing
    [InlineData(ActivityState.Exited, false)]          // gone
    public void IsReadyForDelivery_OnlyWhenIdleAtThePrompt(ActivityState state, bool expected)
        => Assert.Equal(expected, DictationReadiness.IsReadyForDelivery(state));
}
