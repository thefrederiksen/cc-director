using CcDirector.Gateway.Api;
using Xunit;
using static CcDirector.Gateway.Api.NetDiagDrift;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The pure per-device drift Decide-machine (Network Diagnostics mission, Architect Decision 4). These
/// tests are the "never a false alert" guarantee in executable form: warmup, Tailscale-down, away, and
/// no-physical-LAN-presence all self-gate to UNKNOWN; drift only fires for a device the monitor has
/// POSITIVELY confirmed present on the home LAN (ARP + MAC match) that persistently relays for K>=3 ticks
/// over >=5 min, exactly once per episode; and a lost-ability-to-judge (Drifted->Unknown) never emits a
/// false all-clear.
/// </summary>
public sealed class NetDiagDriftTests
{
    private static readonly DateTime T0 = new(2026, 7, 13, 12, 0, 0, DateTimeKind.Utc);
    private static Baseline LanBaseline(double ms = 44) => new() { IsLanDirect = true, TypicalLatencyMs = ms };

    private static Observation Obs(
        bool? direct, bool lanPath, double? latency, DateTime now,
        bool homePresent = false, bool tsUp = true, Baseline? baseline = null) => new()
    {
        TailscaleUp = tsUp,
        Baseline = baseline ?? LanBaseline(),
        CurrentDirect = direct,
        CurrentIsLanPath = lanPath,
        CurrentLatencyMs = latency,
        HomeLanPresent = homePresent,
        NowUtc = now,
    };

    // ---- baseline ----

    [Fact]
    public void ComputeBaseline_TooFewGoodSamples_IsUnknown()
    {
        var samples = Enumerable.Range(0, MinBaselineSamples - 1)
            .Select(_ => new GoodSample(true, true, true, 40)).ToList();
        Assert.Null(ComputeBaseline(samples));
    }

    [Fact]
    public void ComputeBaseline_EnoughHomeDirect_IsLanDirectMedian()
    {
        var samples = new[] { 40.0, 44, 48, 50, 42 }.Select(ms => new GoodSample(true, true, true, ms)).ToList();
        var b = ComputeBaseline(samples);
        Assert.NotNull(b);
        Assert.True(b!.IsLanDirect);
        Assert.Equal(44, b.TypicalLatencyMs); // median of 40,42,44,48,50
    }

    [Fact]
    public void ComputeBaseline_ExcludesRelayAndAwaySamples()
    {
        var samples = new List<GoodSample>
        {
            new(true, true, true, 40), new(true, true, true, 44),
            new(false, true, true, 40),   // away
            new(true, false, false, 200), // relay path
            new(true, true, false, 200),  // not lan path
        };
        Assert.Null(ComputeBaseline(samples)); // only 2 qualify, below the minimum
    }

    // ---- gating: never drift ----

    [Fact]
    public void TailscaleDown_IsUnknown_NeverAlerts()
    {
        var d = Decide(Obs(false, false, null, T0, tsUp: false), new MachineState());
        Assert.Equal("unknown", d.Status);
        Assert.False(d.ShouldAlert);
    }

    [Fact]
    public void NoLanDirectBaseline_IsUnknown()
    {
        var d = Decide(Obs(false, false, null, T0, baseline: null), new MachineState());
        Assert.Equal("unknown", d.Status);
    }

    [Fact]
    public void CurrentlyDirect_IsOk()
    {
        var d = Decide(Obs(true, true, 44, T0, homePresent: true), new MachineState());
        Assert.Equal("ok", d.Status);
        Assert.False(d.ShouldAlert);
    }

    // THE CRUX: relaying but the monitor did NOT positively confirm LAN presence (device left the house, or
    // a different device holds that IP now) = UNKNOWN, never alert - even mid-episode.
    [Fact]
    public void Relaying_NoLanPresence_IsUnknown_NeverAlerts()
    {
        var obs = Obs(false, false, null, T0, homePresent: false);
        var state = new MachineState { State = State.Suspect, ConsecutiveBad = 2, FirstBadUtc = T0 - TimeSpan.FromMinutes(6) };
        var d = Decide(obs, state);
        Assert.Equal("unknown", d.Status);
        Assert.False(d.ShouldAlert);
    }

    // ---- drift accrual + one-shot alert + hysteresis ----

    [Fact]
    public void Relaying_HomePresent_PersistsKAndDuration_DriftsAndAlertsOnce()
    {
        var state = new MachineState();

        var d1 = Decide(Obs(false, false, null, T0, homePresent: true), state);
        Assert.Equal("suspect", d1.Status);
        Assert.False(d1.ShouldAlert);

        var d2 = Decide(Obs(false, false, null, T0.AddMinutes(3), homePresent: true), d1.Next);
        Assert.Equal("suspect", d2.Status);

        var d3 = Decide(Obs(false, false, null, T0.AddMinutes(6), homePresent: true), d2.Next);
        Assert.Equal("drifted", d3.Status);
        Assert.True(d3.ShouldAlert);

        var d4 = Decide(Obs(false, false, null, T0.AddMinutes(9), homePresent: true), d3.Next);
        Assert.Equal("drifted", d4.Status);
        Assert.False(d4.ShouldAlert); // fired once
    }

    [Fact]
    public void KReached_ButUnderFiveMinutes_StaysSuspect()
    {
        var s1 = Decide(Obs(false, false, null, T0, homePresent: true), new MachineState()).Next;
        var s2 = Decide(Obs(false, false, null, T0.AddSeconds(30), homePresent: true), s1).Next;
        var d3 = Decide(Obs(false, false, null, T0.AddMinutes(1), homePresent: true), s2); // 3 bad but 1 min elapsed
        Assert.Equal("suspect", d3.Status);
        Assert.False(d3.ShouldAlert);
    }

    [Fact]
    public void Recovery_AfterAlertedDrift_ResolvesOnce()
    {
        var alerted = new MachineState { State = State.Drifted, ConsecutiveBad = 4, FirstBadUtc = T0, Alerted = true };
        var d = Decide(Obs(true, true, 44, T0.AddMinutes(10), homePresent: true), alerted);
        Assert.Equal("ok", d.Status);
        Assert.True(d.ShouldResolve);
        Assert.False(d.Next.Alerted);
    }

    // Architect review fix: losing the ability to judge (Drifted -> Unknown) must NOT emit a false
    // "your network recovered" all-clear. Only a real observed recovery (Drifted -> Ok) resolves.
    [Fact]
    public void Drifted_ToUnknown_DoesNotResolve()
    {
        var alerted = new MachineState { State = State.Drifted, ConsecutiveBad = 4, FirstBadUtc = T0, Alerted = true };
        var d = Decide(Obs(false, false, null, T0.AddMinutes(10), tsUp: false), alerted); // tailscale down
        Assert.Equal("unknown", d.Status);
        Assert.False(d.ShouldResolve);
        Assert.False(d.ShouldAlert);
    }

    // ---- a STILL-DIRECT device is NEVER drift, whatever its latency (P5 hardening: drift == relay only) ----

    [Fact]
    public void DirectDevice_IsOk_RegardlessOfLatency()
    {
        // Direct on the LAN but slow (200 ms vs a 44 ms baseline) is NOT drift: a relay-framed alert for a
        // direct device would be a misleading diagnosis (wrong cause + wrong fix). "Direct but slow" shows on
        // the dashboard's latency trend, never as a drift / relay alert. The machine keys only on path type.
        Assert.Equal("ok", Decide(Obs(true, true, 200, T0, homePresent: false), new MachineState()).Status);
        Assert.Equal("ok", Decide(Obs(true, true, 50, T0, homePresent: true), new MachineState()).Status);
    }
}
