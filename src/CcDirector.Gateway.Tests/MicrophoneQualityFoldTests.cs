using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pins the microphone-quality verdict, which is the whole point of the background monitoring: a
/// number nobody acts on is worthless, and a warning about a microphone that is fine is worse than
/// worthless because it teaches the user to ignore the screen.
///
/// The calibration behind these thresholds is not a guess. The check was run over 212 REAL dictations
/// from a healthy setup: warning on every imperfection would have complained about roughly one
/// dictation in nine, while warning only on the two unambiguous defects would have said nothing at
/// all. These tests encode that: silence on a good setup, and a clear accusation on a bad one.
/// </summary>
public sealed class MicrophoneQualityFoldTests
{
    private static MicrophoneQualityRecord Sample(
        string device = "Good Mic",
        bool narrowband = false,
        double clipped = 0,
        double speechDb = -20,
        double snrDb = 45,
        int minutesAgo = 0)
        => new()
        {
            TimestampUtc = DateTime.UtcNow.AddMinutes(-minutesAgo),
            Device = device,
            Source = "dictation-send",
            DurationSeconds = 20,
            SampleRate = 48000,
            SpeechLevelDb = speechDb,
            NoiseFloorDb = speechDb - snrDb,
            SignalToNoiseDb = snrDb,
            ClippedFraction = clipped,
            HighBandRatioDb = narrowband ? -110 : -22,
            Narrowband = narrowband,
            Rating = narrowband || clipped >= 0.01 ? "poor" : "good",
            Issues = narrowband ? "narrowband" : clipped >= 0.01 ? "clipping" : "",
        };

    private static List<MicrophoneQualityRecord> Many(int count, Func<int, MicrophoneQualityRecord> make)
        => Enumerable.Range(0, count).Select(make).ToList();

    [Fact]
    public void NoMeasurementsYet_SaysSo_AndDoesNotPretendAnythingIsWrong()
    {
        var s = MicrophoneQualityFold.Summarize(Array.Empty<MicrophoneQualityRecord>());

        Assert.Equal("empty", s.Status);
        Assert.Equal(0, s.TotalSamples);
        Assert.Empty(s.Devices);
        Assert.Contains("No dictation has been measured", s.Headline);
    }

    [Fact]
    public void AHealthyMicrophoneIsNeverComplainedAbout()
    {
        // The property that matters most. This is the shape of the real 212-clip sample.
        var s = MicrophoneQualityFold.Summarize(Many(40, _ => Sample()));

        Assert.Equal("good", s.Status);
        Assert.Contains("sounds good", s.Headline);
        Assert.Equal("good", Assert.Single(s.Devices).Status);
    }

    [Fact]
    public void TooFewMeasurementsIsHeldBack_RatherThanJudgedOnOneOddRecording()
    {
        var s = MicrophoneQualityFold.Summarize(Many(MicrophoneQualityFold.MinSamplesForVerdict - 1, _ => Sample(narrowband: true)));

        // Narrowband on every clip, but not enough of them: it must NOT accuse yet.
        Assert.Equal("learning", s.Status);
        Assert.Equal("learning", Assert.Single(s.Devices).Status);
    }

    [Fact]
    public void AMostlyBandLimitedMicrophoneIsNamedAndExplained()
    {
        var s = MicrophoneQualityFold.Summarize(Many(20, _ => Sample(device: "Jabra Hands-Free", narrowband: true)));

        Assert.Equal("bad", s.Status);
        Assert.Contains("Jabra Hands-Free", s.Headline);
        Assert.Contains("Bluetooth", s.Detail);
    }

    [Fact]
    public void OccasionalBandLimitingIsNotEnoughToAccuse()
    {
        // A Bluetooth link that dropped to hands-free for one call is not a bad headset.
        var s = MicrophoneQualityFold.Summarize(Many(20, i => Sample(narrowband: i < 3)));

        Assert.Equal("good", s.Status);
    }

    [Fact]
    public void PersistentDistortionIsCalledOut()
    {
        var s = MicrophoneQualityFold.Summarize(Many(20, i => Sample(device: "Hot Mic", clipped: i < 10 ? 0.05 : 0)));

        Assert.Equal("bad", s.Status);
        Assert.Contains("Hot Mic", s.Headline);
        Assert.Contains("input level", s.Detail);
    }

    [Fact]
    public void QuietOrNoisyAudioAloneNeverProducesAWarning()
    {
        // Deliberate: on the real sample these fired on about one dictation in nine. They are shown
        // as measurements on the device card, never as an accusation.
        var s = MicrophoneQualityFold.Summarize(Many(30, _ => Sample(speechDb: -35, snrDb: 14)));

        Assert.Equal("good", s.Status);
        Assert.Equal("good", Assert.Single(s.Devices).Status);
    }

    [Fact]
    public void TheWorstMicrophoneLeadsRatherThanTheAverageOfAllOfThem()
    {
        // The finding a user needs is "one of your microphones is the problem". Averaging a bad
        // headset with a good desk microphone would hide exactly that.
        var records = Many(20, _ => Sample(device: "Good Desk Mic"))
            .Concat(Many(20, _ => Sample(device: "Bad Headset", narrowband: true)))
            .ToList();

        var s = MicrophoneQualityFold.Summarize(records);

        Assert.Equal("bad", s.Status);
        Assert.Contains("Bad Headset", s.Headline);
        Assert.Equal(2, s.Devices.Count);
        Assert.Equal("good", s.Devices.Single(d => d.Device == "Good Desk Mic").Status);
        Assert.Equal("bad", s.Devices.Single(d => d.Device == "Bad Headset").Status);
    }

    [Fact]
    public void EachDeviceIsScoredSeparately_WhichIsThePointOfRecordingTheName()
    {
        var records = Many(10, _ => Sample(device: "A"))
            .Concat(Many(6, _ => Sample(device: "B", clipped: 0.05)))
            .ToList();

        var s = MicrophoneQualityFold.Summarize(records);

        Assert.Equal(1.0, s.Devices.Single(d => d.Device == "B").ClippingShare);
        Assert.Equal(0.0, s.Devices.Single(d => d.Device == "A").ClippingShare);
    }

    [Fact]
    public void AnUnnamedMicrophoneIsStillReported_UnderAReadableName()
    {
        var s = MicrophoneQualityFold.Summarize(Many(10, _ => Sample(device: "")));

        Assert.Equal("Unnamed microphone", Assert.Single(s.Devices).Device);
    }

    [Fact]
    public void EveryDeviceCarriesWhatGoodLooksLike_SoTheScreenCanCompare()
    {
        var device = Assert.Single(MicrophoneQualityFold.Summarize(Many(10, _ => Sample())).Devices);

        Assert.Equal(MicrophoneQualityFold.TargetSpeechLevelDb, device.TargetSpeechLevelDb);
        Assert.Equal(MicrophoneQualityFold.TargetSignalToNoiseDb, device.TargetSignalToNoiseDb);
    }

    [Fact]
    public void TheMedianIsReported_SoOneOddRecordingCannotMoveTheNumber()
    {
        var records = Many(20, _ => Sample(speechDb: -20)).Append(Sample(speechDb: -90)).ToList();

        var device = Assert.Single(MicrophoneQualityFold.Summarize(records).Devices);

        Assert.Equal(-20, device.MedianSpeechLevelDb);
    }
}
