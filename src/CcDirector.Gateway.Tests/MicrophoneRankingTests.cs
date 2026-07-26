using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Pins WHICH microphone the daily report will tell somebody to use.
///
/// This is the part of the report that makes a recommendation about hardware the user owns, so the
/// order has to reflect what actually costs a transcript rather than what looks worst on a chart. The
/// ordering claim is: band-limiting dominates everything, distortion comes next, then how far the
/// voice stands above the room, then level as a tie-break only.
/// </summary>
public sealed class MicrophoneRankingTests
{
    private static MicrophoneDeviceSummary Device(
        string name,
        int samples = 20,
        double narrowband = 0,
        double clipping = 0,
        double speechDb = -20,
        double snrDb = 45,
        string status = "good")
        => new()
        {
            Device = name,
            Samples = samples,
            Status = status,
            Advice = "",
            NarrowbandShare = narrowband,
            ClippingShare = clipping,
            MedianSpeechLevelDb = speechDb,
            MedianSignalToNoiseDb = snrDb,
            TargetSpeechLevelDb = MicrophoneQualityFold.TargetSpeechLevelDb,
            TargetSignalToNoiseDb = MicrophoneQualityFold.TargetSignalToNoiseDb,
            LastSeenUtc = DateTime.UtcNow,
        };

    [Fact]
    public void ABandLimitedMicrophoneRanksBelowAWidebandOne_EvenWhenItIsLouderAndCleaner()
    {
        // The case that motivated the whole feature: on every other reading the headset looks BETTER.
        // A ranking that trusted level and quiet would recommend exactly the wrong device.
        var ranked = MicrophoneQualityFold.RankBest(new[]
        {
            Device("Bluetooth Headset", narrowband: 0.9, speechDb: -14, snrDb: 68),
            Device("Laptop Microphone", narrowband: 0, speechDb: -28, snrDb: 30),
        });

        Assert.Equal("Laptop Microphone", ranked[0].Device);
    }

    [Fact]
    public void DistortionRanksBelowACleanMicrophone()
    {
        var ranked = MicrophoneQualityFold.RankBest(new[]
        {
            Device("Hot Mic", clipping: 0.6),
            Device("Clean Mic"),
        });

        Assert.Equal("Clean Mic", ranked[0].Device);
    }

    [Fact]
    public void BandLimitingOutweighsDistortion()
    {
        // Distortion corrupts what is there; band-limiting removes it. Given a choice, prefer the
        // distorting microphone - its vowels survive.
        var ranked = MicrophoneQualityFold.RankBest(new[]
        {
            Device("Band Limited", narrowband: 1.0),
            Device("Distorting", clipping: 1.0),
        });

        Assert.Equal("Distorting", ranked[0].Device);
    }

    [Fact]
    public void AQuieterRoomStopsHelpingOnceTheVoiceIsAlreadyClearOfIt()
    {
        // Without the cap, a silent room would let a worse microphone outrank a better one purely on
        // an enormous signal-to-noise number that stopped meaning anything long ago.
        var ranked = MicrophoneQualityFold.RankBest(new[]
        {
            Device("Silent Room", snrDb: 90, clipping: 0.2),
            Device("Normal Room", snrDb: 30, clipping: 0),
        });

        Assert.Equal("Normal Room", ranked[0].Device);
    }

    [Fact]
    public void LevelIsOnlyATieBreak_AndNeverReordersMicrophonesThatDifferOnAnythingElse()
    {
        var ranked = MicrophoneQualityFold.RankBest(new[]
        {
            Device("Perfect Level But Band Limited", speechDb: -20, narrowband: 0.8),
            Device("Quiet But Clean", speechDb: -34),
        });

        Assert.Equal("Quiet But Clean", ranked[0].Device);
    }

    [Fact]
    public void LevelDoesDecideBetweenOtherwiseIdenticalMicrophones()
    {
        var ranked = MicrophoneQualityFold.RankBest(new[]
        {
            Device("Too Quiet", speechDb: -40),
            Device("Just Right", speechDb: -20),
        });

        Assert.Equal("Just Right", ranked[0].Device);
    }

    [Fact]
    public void ADeviceWithTooFewMeasurementsIsNotRankedAtAll()
    {
        // Recommending hardware on two recordings would be recommending on noise.
        var ranked = MicrophoneQualityFold.RankBest(new[]
        {
            Device("Barely Used", samples: MicrophoneQualityFold.MinSamplesForVerdict - 1),
            Device("Well Used"),
        });

        Assert.Equal("Well Used", Assert.Single(ranked).Device);
    }

    [Fact]
    public void TheOrderIsStable_SoTheRecommendationDoesNotFlipBetweenReports()
    {
        // Two identical devices must not swap places from one report to the next.
        var devices = new[] { Device("B Mic"), Device("A Mic") };

        var first = MicrophoneQualityFold.RankBest(devices).Select(d => d.Device);
        var second = MicrophoneQualityFold.RankBest(devices.Reverse().ToArray()).Select(d => d.Device);

        Assert.Equal(first, second);
    }

    [Fact]
    public void RankingNothingReturnsNothingRatherThanThrowing()
    {
        Assert.Empty(MicrophoneQualityFold.RankBest(Array.Empty<MicrophoneDeviceSummary>()));
        Assert.Empty(MicrophoneQualityFold.RankBest(null!));
    }

    [Fact]
    public void TheScoreNeverGoesNegative_SoAThoroughlyBadMicrophoneStillOrdersSensibly()
    {
        var awful = Device("Awful", narrowband: 1, clipping: 1, speechDb: -60, snrDb: 0);
        Assert.True(MicrophoneQualityFold.ComparableScore(awful) >= 0);
    }
}
