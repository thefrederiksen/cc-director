using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The DevThrottle Stats tally math: one submitted turn is one turn (never inflated by mechanics),
/// including a submission that carried no new characters, buckets split by (modality, surface), and a
/// snapshot round-trips through the wire DTO so a persisted tally restores exactly after a Director
/// restart.
/// </summary>
public sealed class SessionInputStatsTests
{
    private static long Turns(InputStatsDtoView v, string modality, string surface) =>
        v.Get(modality, surface).Turns;

    private static long Chars(InputStatsDtoView v, string modality, string surface) =>
        v.Get(modality, surface).Characters;

    [Fact]
    public void RecordTurn_CountsOneTurnAndCharacters_PerBucket()
    {
        var stats = new SessionInputStats();

        stats.RecordTurn(InputOrigin.Voice(InputSurface.Phone), characters: 40);
        stats.RecordTurn(InputOrigin.Voice(InputSurface.Phone), characters: 60);
        stats.RecordTurn(InputOrigin.DesktopTyped, characters: 10);

        var v = new InputStatsDtoView(stats.Snapshot());

        Assert.Equal(2, Turns(v, "voice", "phone"));
        Assert.Equal(100, Chars(v, "voice", "phone"));
        Assert.Equal(1, Turns(v, "typed", "desktop"));
        Assert.Equal(10, Chars(v, "typed", "desktop"));
    }

    [Fact]
    public void ASubmissionWithNoNewCharacters_IsStillExactlyOneTurn()
    {
        // A line recalled from the terminal's history is submitted without a single new printable
        // keystroke. It is a turn, and it must be counted as one, because the submission ledger written
        // in the same breath counts it: a tally that drops the zero-character turns disagrees with the
        // ledger by exactly the turns it dropped, which is the class of drift this whole slice removes.
        var stats = new SessionInputStats();

        stats.RecordTurn(InputOrigin.DesktopTyped, characters: 0);
        stats.RecordTurn(InputOrigin.DesktopTyped, characters: 12);

        var v = new InputStatsDtoView(stats.Snapshot());

        Assert.Equal(2, Turns(v, "typed", "desktop"));
        Assert.Equal(12, Chars(v, "typed", "desktop"));
    }

    [Fact]
    public void OneSpokenTurnAndOneTypedTurn_EachCountAsExactlyOneTurn()
    {
        // The fairness property the mission's headline unit rests on: a long dictated utterance and a
        // short typed message are each ONE turn, so neither modality is inflated by its mechanics.
        var stats = new SessionInputStats();

        stats.RecordTurn(InputOrigin.Voice(InputSurface.Phone), characters: 500);
        stats.RecordTurn(InputOrigin.Typed(InputSurface.Desktop), characters: 4);

        var v = new InputStatsDtoView(stats.Snapshot());

        Assert.Equal(1, Turns(v, "voice", "phone"));
        Assert.Equal(1, Turns(v, "typed", "desktop"));
    }

    [Fact]
    public void RecordTurn_IgnoresNonPositiveVolume_ButStillCountsTheTurn()
    {
        var stats = new SessionInputStats();

        stats.RecordTurn(InputOrigin.DesktopTyped, characters: 0);
        stats.RecordTurn(InputOrigin.DesktopTyped, characters: -3);

        var v = new InputStatsDtoView(stats.Snapshot());

        Assert.False(stats.IsEmpty);
        Assert.Equal(2, Turns(v, "typed", "desktop"));
        Assert.Equal(0, Chars(v, "typed", "desktop"));
    }

    [Fact]
    public void Snapshot_RoundTripsThroughSeed_RestoresExactCounts()
    {
        var original = new SessionInputStats();
        original.RecordTurn(InputOrigin.Voice(InputSurface.Phone), 100);
        original.RecordTurn(InputOrigin.Typed(InputSurface.Cockpit), 20);
        original.RecordTurn(InputOrigin.Typed(InputSurface.Desktop), 33);

        var persisted = original.Snapshot();

        var restored = new SessionInputStats();
        restored.Seed(persisted);
        var v = new InputStatsDtoView(restored.Snapshot());

        Assert.Equal(1, Turns(v, "voice", "phone"));
        Assert.Equal(100, Chars(v, "voice", "phone"));
        Assert.Equal(1, Turns(v, "typed", "cockpit"));
        Assert.Equal(20, Chars(v, "typed", "cockpit"));
        Assert.Equal(1, Turns(v, "typed", "desktop"));
        Assert.Equal(33, Chars(v, "typed", "desktop"));
    }

    [Fact]
    public void Seed_ClearsBucketsNotInSnapshot()
    {
        var stats = new SessionInputStats();
        stats.RecordTurn(InputOrigin.Voice(InputSurface.Phone), 10);

        stats.Seed(new InputStatsDto()); // empty snapshot

        Assert.True(stats.IsEmpty);
    }

    [Fact]
    public void UnknownSurface_IsRecordedHonestly_NotFoldedAway()
    {
        var stats = new SessionInputStats();
        stats.RecordTurn(InputOrigin.Typed(InputSurface.Unknown), 5);

        var v = new InputStatsDtoView(stats.Snapshot());
        Assert.Equal(1, Turns(v, "typed", "unknown"));
    }

    [Theory]
    [InlineData("phone", InputSurface.Phone)]
    [InlineData("browser", InputSurface.Cockpit)]
    [InlineData("BROWSER", InputSurface.Cockpit)]
    [InlineData("workstation", InputSurface.Unknown)]
    [InlineData("unknown", InputSurface.Unknown)]
    [InlineData("", InputSurface.Unknown)]
    [InlineData(null, InputSurface.Unknown)]
    public void RemoteSurfaceFromDeviceType_MapsKnownDevices_AndDoesNotGuessTheRest(string? deviceType, InputSurface expected)
    {
        // A remote operator prompt with an unresolved surface ("unknown"/null) resolves to Unknown - which is
        // COUNTED into the Unknown bucket (surfaced on the dashboard), never dropped or guessed (decision 9).
        Assert.Equal(expected, InputOrigin.RemoteSurfaceFromDeviceType(deviceType));
    }

    /// <summary>Tiny read helper: look up a bucket by its wire tokens, defaulting to zeroes when absent.</summary>
    private sealed class InputStatsDtoView
    {
        private readonly InputStatsDto _dto;
        public InputStatsDtoView(InputStatsDto dto) => _dto = dto;

        public InputStatBucketDto Get(string modality, string surface) =>
            _dto.Buckets.FirstOrDefault(b =>
                string.Equals(b.Modality, modality, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(b.Surface, surface, StringComparison.OrdinalIgnoreCase))
            ?? new InputStatBucketDto { Modality = modality, Surface = surface };
    }
}
