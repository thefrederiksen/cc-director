using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #1181, Task 4: the phone-dictation presentation sub-state on the SessionDto fold. The Gateway
/// stamps <see cref="SessionDto.DictationStatus"/> ("Uploading from phone" while the phone is still
/// sending, "Transcribing" while the server works, null otherwise); these assert that the shared fold
/// turns it into the right label and the orange color, and that the legacy path still works.
/// </summary>
public sealed class DictationPresentationTests
{
    private static SessionDto Base(string color = "red") => new()
    {
        SessionId = "s1",
        StatusColor = color,
        ActivityState = color == "red" ? "WaitingForInput" : "Working",
        BriefingState = "None",
    };

    [Fact]
    public void StateLabel_UploadingFromPhone_ShowsThatPhase()
    {
        var s = Base();
        s.DictationStatus = "Uploading from phone";
        Assert.Equal("Uploading from phone", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void StateLabel_Transcribing_ShowsThatPhase()
    {
        var s = Base();
        s.DictationStatus = "Transcribing";
        Assert.Equal("Transcribing", SessionOrdering.StateLabel(s));
    }

    [Theory]
    [InlineData("Uploading from phone")]
    [InlineData("Transcribing")]
    public void EffectiveColor_IsOrange_WhileADictationIsInbound_EvenOverRed(string phase)
    {
        var s = Base("red"); // would be red "needs you" without the dictation
        s.DictationStatus = phase;
        Assert.Equal("orange", SessionOrdering.EffectiveColor(s));
    }

    [Fact]
    public void StateLabel_FallsBackToTranscribing_ForLegacyFlagWithoutPhase()
    {
        var s = Base();
        s.Transcribing = true; // legacy blanket flag, no DictationStatus
        Assert.Equal("Transcribing", SessionOrdering.StateLabel(s));
    }

    [Fact]
    public void NoDictation_IsNotOrange_AndKeepsBaseLabel()
    {
        var s = Base("red");
        Assert.NotEqual("orange", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Needs you", SessionOrdering.StateLabel(s));
    }
}
