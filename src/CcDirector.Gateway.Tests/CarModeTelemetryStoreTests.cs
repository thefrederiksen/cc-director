using CcDirector.Gateway.CarMode;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Car Mode telemetry store (performance round) keeps per-turn timing records, retained by AGE
/// (90 days) with a growth-guard cap only. These tests drive it against a temp file to prove it records,
/// hands back newest-first, prunes records older than the retention window, keeps records with an
/// unparseable stamp, survives a corrupt file, and never stores any text.
/// </summary>
public sealed class CarModeTelemetryStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"carmode-telemetry-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*"))
            File.Delete(f);
    }

    private static CarModeTelemetryRecord Record(string turnId, string receivedAtUtc, double totalTurnMs = 2500) => new()
    {
        TurnId = turnId,
        ReceivedAtUtc = receivedAtUtc,
        DeviceHash = "abc123def456",
        GatewayVersion = "1.0.0+test",
        TotalTurnMs = totalTurnMs,
        BrainMs = 1200,
        FleetReadCount = 0,
        ModelCallCount = 1,
    };

    [Fact]
    public void Add_ThenRecent_ReturnsNewestFirst()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(Record("t1", DateTime.UtcNow.AddMinutes(-2).ToString("o")));
        store.Add(Record("t2", DateTime.UtcNow.AddMinutes(-1).ToString("o")));

        var recent = store.Recent(10);

        Assert.Equal(2, recent.Count);
        Assert.Equal("t2", recent[0].TurnId); // newest first
        Assert.Equal("t1", recent[1].TurnId);
        Assert.Equal(2, store.Count());
    }

    [Fact]
    public void Prune_DropsRecordsOlderThanNinetyDays_KeepsRecent()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(Record("old", DateTime.UtcNow.AddDays(-120).ToString("o")));
        store.Add(Record("fresh", DateTime.UtcNow.AddDays(-1).ToString("o")));

        var recent = store.Recent(10);

        Assert.Single(recent);
        Assert.Equal("fresh", recent[0].TurnId);
    }

    [Fact]
    public void Prune_KeepsRecordWithUnparseableStamp_RatherThanDiscardIt()
    {
        var store = new CarModeTelemetryStore(_path, _ => { });
        store.Add(Record("weird", "not-a-date"));

        Assert.Equal(1, store.Count());
    }

    [Fact]
    public void Load_RestoresPersistedRecords_AcrossInstances()
    {
        var first = new CarModeTelemetryStore(_path, _ => { });
        first.Add(Record("t1", DateTime.UtcNow.ToString("o")));

        var second = new CarModeTelemetryStore(_path, _ => { });

        Assert.Equal(1, second.Count());
        Assert.Equal("t1", second.Recent(1)[0].TurnId);
    }

    [Fact]
    public void Add_ThenRecent_RoundTripsTheCutOffReplyLifecycleFields()
    {
        // The cut-off-reply diagnostic: a reply that was synthesized but NOT heard to the end must persist
        // as Completed=false with its played duration and clip count, so a truncated reply is visible at
        // /carmode/telemetry the next time it happens - the whole reason these fields were added.
        var store = new CarModeTelemetryStore(_path, _ => { });
        var cutOff = Record("cut", DateTime.UtcNow.ToString("o")) with
        {
            Chunks = 1,
            PlayMs = 640,
            Completed = false,
            ReplyChars = 180,
        };
        store.Add(cutOff);

        var reloaded = new CarModeTelemetryStore(_path, _ => { }).Recent(1)[0];

        Assert.False(reloaded.Completed); // the reply did not play to its end
        Assert.Equal(1, reloaded.Chunks);
        Assert.Equal(640, reloaded.PlayMs);
        Assert.Equal(180, reloaded.ReplyChars);
    }

    [Fact]
    public void Add_ThenRecent_RoundTripsTheMobileDiagnosticFields()
    {
        // The mobile-failure diagnostics (this round): the "over and out" finickiness (how many transcribe
        // tries before the turn was taken) and the mic-contention hypothesis fields (the whole synthesized
        // clip length, how far playback reached, whether the mic was re-opened mid-playback, and how many
        // rolling "stop" polls ran) must all persist so ONE real phone turn is enough to read the truth.
        var store = new CarModeTelemetryStore(_path, _ => { });
        var turn = Record("mobile", DateTime.UtcNow.ToString("o")) with
        {
            TranscribeAttempts = 4,             // "over and out" took four tries to land - finicky
            ClipDurationMs = 5200,              // the whole reply the phone received
            PlayedToMs = 2100,                  // but playback only reached 2.1s in
            Completed = false,
            MicReacquiredDuringPlayback = true, // the mic was re-opened while the reply played
            SpeakingPollCount = 2,
        };
        store.Add(turn);

        var reloaded = new CarModeTelemetryStore(_path, _ => { }).Recent(1)[0];

        Assert.Equal(4, reloaded.TranscribeAttempts);
        Assert.Equal(5200, reloaded.ClipDurationMs);
        Assert.Equal(2100, reloaded.PlayedToMs);
        Assert.True(reloaded.MicReacquiredDuringPlayback);
        Assert.Equal(2, reloaded.SpeakingPollCount);
    }

    [Fact]
    public void Load_QuarantinesCorruptFile_AndStartsEmpty()
    {
        File.WriteAllText(_path, "{ this is not valid json ][");

        var store = new CarModeTelemetryStore(_path, _ => { });

        Assert.Equal(0, store.Count());
        var corrupt = Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + ".corrupt-*");
        Assert.Single(corrupt);
    }
}

/// <summary>The device hash (DT-05): a device's turns must group by a stable one-way hash that never
/// contains the raw credential, and a blank credential maps to a fixed anonymous bucket.</summary>
public sealed class CarModeDeviceHashTests
{
    [Fact]
    public void Of_IsStableForTheSameCredential_ButNeverContainsIt()
    {
        var secret = "super-secret-device-key-1234567890";
        var a = CarModeDeviceHash.Of(secret);
        var b = CarModeDeviceHash.Of(secret);

        Assert.Equal(a, b);
        Assert.DoesNotContain(secret, a, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", a, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(12, a.Length); // 6 bytes rendered as hex
    }

    [Fact]
    public void Of_DifferentCredentials_HashDifferently()
    {
        Assert.NotEqual(CarModeDeviceHash.Of("device-one"), CarModeDeviceHash.Of("device-two"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Of_BlankCredential_MapsToAnonymous(string? credential)
    {
        Assert.Equal("anonymous", CarModeDeviceHash.Of(credential));
    }
}
