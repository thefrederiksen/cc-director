using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Reports;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The microphone section of the daily report.
///
/// The rule this section is held to is the one the rest of the report already keeps: a section the
/// Gateway has no data for is ABSENT, never empty and never zero-filled. "We have never measured your
/// microphones" and "your microphones are fine" are different statements, and a daily email that
/// confuses them tells the reader something untrue every single morning.
///
/// The second rule is about nagging. This arrives in an inbox once a day whether or not anything is
/// wrong, so advice is present ONLY when acting on it would help. A report that always has a
/// recommendation is one the reader learns to skip.
/// </summary>
public sealed class MorningReportMicrophonesTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private readonly string _root;

    private static readonly TenantId Alice = new("tenant-alice");
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    public MorningReportMicrophonesTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "cc-report-mics-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _h.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private static MorningReportWindow Window() => MorningReportWindow.Resolve("2026-07-23", "America/Toronto");

    /// <summary>A per-tenant log rooted in this test's temp directory.</summary>
    private Func<TenantId, MicrophoneQualityLog> Logs()
        => tenant => new MicrophoneQualityLog(Path.Combine(_root, tenant.Value));

    private MorningReportBuilder Builder(bool withMicrophones = true)
        => new(_h.Open(new FixedTenantContext(Alice)), null, TimeSpan.FromMinutes(5), () => Now,
               microphoneQuality: withMicrophones ? Logs() : null);

    private void Seed(TenantId tenant, string device, int count, bool narrowband = false, double clipped = 0,
        double speechDb = -20, double snrDb = 45, int daysAgo = 1, string deviceId = "", string platform = "")
    {
        var log = Logs()(tenant);
        for (var i = 0; i < count; i++)
        {
            log.Record(new MicrophoneQualityRecord
            {
                TimestampUtc = Now.AddDays(-daysAgo),
                Device = device,
                DeviceId = deviceId,
                Platform = platform,
                Source = "dictation-send",
                DurationSeconds = 20,
                SampleRate = 48000,
                SpeechLevelDb = speechDb,
                NoiseFloorDb = speechDb - snrDb,
                SignalToNoiseDb = snrDb,
                ClippedFraction = clipped,
                HighBandRatioDb = narrowband ? -110 : -22,
                Narrowband = narrowband,
                Rating = narrowband ? "poor" : "good",
                Issues = narrowband ? "narrowband" : "",
            });
        }
    }

    [Fact]
    public void TheSectionIsAbsentWhenNothingHasBeenMeasured()
    {
        var report = Builder().Build("alice@example.com", Alice, Window());

        Assert.Null(report.Microphones);
    }

    [Fact]
    public void TheSectionIsAbsentWhenNoDeviceHasEnoughMeasurementsToJudge()
    {
        // Present-but-unjudgeable is still "we do not know", and must read the same as no data.
        Seed(Alice, "Barely Used", MicrophoneQualityFold.MinSamplesForVerdict - 1);

        Assert.Null(Builder().Build("alice@example.com", Alice, Window()).Microphones);
    }

    [Fact]
    public void TheSectionIsAbsentWhenTheDeploymentHasNoMicrophoneLogAtAll()
    {
        Seed(Alice, "Good Mic", 20);

        Assert.Null(Builder(withMicrophones: false).Build("alice@example.com", Alice, Window()).Microphones);
    }

    [Fact]
    public void AHealthyMicrophoneIsReportedWithNoAdvice()
    {
        // The anti-nag rule: nothing to act on means no recommendation in the inbox.
        Seed(Alice, "Good Mic", 20);

        var mics = Builder().Build("alice@example.com", Alice, Window()).Microphones;

        Assert.NotNull(mics);
        Assert.Null(mics!.Advice);
        Assert.Contains("doing fine", mics.Headline);
        Assert.Equal("good", Assert.Single(mics.Devices).Status);
    }

    [Fact]
    public void TheBestMicrophoneIsNamedAndTheWorstIsCalledOut()
    {
        // The comparison the owner actually asked for: which of my devices should I be using.
        Seed(Alice, "Desk Mic", 20);
        Seed(Alice, "Bluetooth Headset", 20, narrowband: true);

        var mics = Builder().Build("alice@example.com", Alice, Window()).Microphones;

        Assert.NotNull(mics);
        Assert.Equal("Desk Mic", mics!.Devices[0].Device);
        Assert.Contains("Desk Mic", mics.Headline);
        Assert.Contains("Bluetooth Headset", mics.Headline);
        Assert.Contains("Use Desk Mic", mics.Advice);
    }

    [Fact]
    public void WhenEveryMicrophoneIsBadItDoesNotRecommendOneOfThem()
    {
        // Naming a "best" here would be recommending a microphone we have just said is bad.
        Seed(Alice, "Bad One", 20, narrowband: true);
        Seed(Alice, "Also Bad", 20, narrowband: true);

        var mics = Builder().Build("alice@example.com", Alice, Window()).Microphones;

        Assert.NotNull(mics);
        Assert.Contains("Every microphone", mics!.Headline);
        Assert.DoesNotContain("Use ", mics.Advice ?? "");
    }

    [Fact]
    public void DevicesArriveRankedBestFirst()
    {
        Seed(Alice, "Worst", 20, narrowband: true);
        Seed(Alice, "Middle", 20, clipped: 0.5);
        Seed(Alice, "Best", 20);

        var devices = Builder().Build("alice@example.com", Alice, Window()).Microphones!.Devices;

        Assert.Equal(new[] { "Best", "Middle", "Worst" }, devices.Select(d => d.Device).ToArray());
    }

    [Fact]
    public void EachDeviceCarriesTheNumbersBehindItsVerdict()
    {
        Seed(Alice, "Headset", 20, narrowband: true, speechDb: -18, snrDb: 40);

        var device = Assert.Single(Builder().Build("alice@example.com", Alice, Window()).Microphones!.Devices);

        Assert.Equal(20, device.Samples);
        Assert.Equal(1.0, device.NarrowbandShare);
        Assert.Equal(-18, device.SpeechLevelDb);
        Assert.Equal(40, device.SignalToNoiseDb);
        Assert.NotEmpty(device.Summary);
    }

    [Fact]
    public void OneAccountsMicrophonesNeverAppearInAnothersReport()
    {
        Seed(Alice, "Alice Mic", 20);
        Seed(new TenantId("tenant-bob"), "Bob Mic", 20, narrowband: true);

        var mics = Builder().Build("alice@example.com", Alice, Window()).Microphones;

        Assert.Equal("Alice Mic", Assert.Single(mics!.Devices).Device);
    }

    [Fact]
    public void TheSectionLooksWiderThanTheReportedDay_SoAQuietDayDoesNotEraseAVerdict()
    {
        // A microphone is a property of the desk, not of yesterday. These measurements are 10 days
        // before the reported day and must still count.
        Seed(Alice, "Good Mic", 20, daysAgo: 10);

        Assert.NotNull(Builder().Build("alice@example.com", Alice, Window()).Microphones);
    }

    [Fact]
    public void MeasurementsOlderThanTheWindowAreNotReported()
    {
        Seed(Alice, "Ancient Mic", 20, daysAgo: 60);

        Assert.Null(Builder().Build("alice@example.com", Alice, Window()).Microphones);
    }

    [Fact]
    public void TheBestAndWorstAreNamedWithTheirPlatform_SoTheComparisonHasContext()
    {
        // "Your phone microphone beats your Windows headset" is the sentence the owner asked for;
        // two bare names with no platform cannot say it.
        Seed(Alice, "iPhone Microphone", 20, deviceId: "id-phone", platform: "mobile");
        Seed(Alice, "Bluetooth Headset", 20, deviceId: "id-headset", platform: "windows", narrowband: true);

        var mics = Builder().Build("alice@example.com", Alice, Window()).Microphones;

        Assert.NotNull(mics);
        Assert.Contains("iPhone Microphone (Phone or tablet)", mics!.Headline);
        Assert.Contains("Bluetooth Headset (Windows)", mics.Headline);
        Assert.Equal("mobile", mics.Devices[0].Platform);
        Assert.Equal("Phone or tablet", mics.Devices[0].PlatformLabel);
        Assert.Equal("windows", mics.Devices[^1].Platform);
    }

    [Fact]
    public void AnUnknownPlatformAddsNothingToTheSentence()
    {
        // Legacy measurements carry no platform. "(unknown)" next to a working microphone is noise.
        Seed(Alice, "Good Mic", 20);

        var mics = Builder().Build("alice@example.com", Alice, Window()).Microphones;

        Assert.NotNull(mics);
        Assert.Contains("Good Mic is doing fine.", mics!.Headline);
        Assert.DoesNotContain("(", mics.Headline);
        Assert.Equal("unknown", Assert.Single(mics.Devices).Platform);
        Assert.Equal("", Assert.Single(mics.Devices).PlatformLabel);
    }

    [Fact]
    public void TheEmailStaysASummary_AndPointsTheReaderAtTheCockpitForTheDetail()
    {
        Seed(Alice, "Good Mic", 20);

        var mics = Builder().Build("alice@example.com", Alice, Window()).Microphones;

        Assert.NotNull(mics);
        Assert.Contains("Transcription Health", mics!.DetailHint);
    }
}
