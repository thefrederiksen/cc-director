namespace CcDirector.Gateway.Api;

/// <summary>
/// Pure per-device drift detection (Network Diagnostics mission, Phase 1 / Architect Decision 4). Built
/// ONCE here: the P1 server-side monitor feeds it and it LOGS drift state quietly (no channels); P5 later
/// bolts the doorbell + owner-email onto the same machine without changing this logic.
///
/// The whole point is to avoid false alarms. Two ideas make it honest:
///  - Per-device baseline from HOME+DIRECT samples ONLY, UNKNOWN until enough good samples, so one relay
///    sample cannot poison a device's "good" number and warmup never drifts.
///  - The drift discriminator keys off PATH TYPE vs the device's baseline path, not raw latency: a device
///    whose baseline is LAN-direct now showing a relay (DERP / not-direct) is the unambiguous drift signal;
///    latency-worse-than-baseline is only a SECONDARY signal with a generous margin. This cleanly separates
///    "a home device fell to the relay" (DRIFT) from "a device is genuinely away" (NOT drift - never flagged,
///    because an away device has no LAN-direct baseline to fall from, so the machine self-gates to Unknown).
///
/// Everything here is a pure function of (observation, state) so it is fully unit-testable with no clock,
/// no CLI, and no I/O.
/// </summary>
public static class NetDiagDrift
{
    /// <summary>Good HOME+DIRECT samples required before a device's baseline is known (until then: Unknown, never drift).</summary>
    public const int MinBaselineSamples = 5;

    /// <summary>K: consecutive bad observations required before drift is declared.</summary>
    public const int ConsecutiveBadToDrift = 3;

    /// <summary>T: drift must persist at least this long (with the cold-start relay window well under it).</summary>
    public static readonly TimeSpan MinDriftDuration = TimeSpan.FromMinutes(5);

    /// <summary>A device's established "good" state, derived from HOME+DIRECT history only.</summary>
    public sealed record Baseline
    {
        /// <summary>True when this device is normally on a direct LAN path (the only kind that can "drift" to relay).</summary>
        public bool IsLanDirect { get; init; }
        public double TypicalLatencyMs { get; init; }
    }

    /// <summary>One historical sample used to build a baseline.</summary>
    public readonly record struct GoodSample(bool IsHome, bool Direct, bool IsLanPath, double LatencyMs);

    /// <summary>
    /// Compute a per-device baseline from its samples, or null (UNKNOWN) until it has at least
    /// <see cref="MinBaselineSamples"/> HOME + DIRECT + LAN-path samples. Relay/away samples are excluded.
    /// </summary>
    public static Baseline? ComputeBaseline(IReadOnlyList<GoodSample> samples)
    {
        var good = samples.Where(s => s.IsHome && s.Direct && s.IsLanPath).Select(s => s.LatencyMs).ToList();
        if (good.Count < MinBaselineSamples) return null;
        return new Baseline { IsLanDirect = true, TypicalLatencyMs = Median(good) };
    }

    public enum State { Unknown, Ok, Suspect, Drifted }

    /// <summary>The carried state between monitor ticks for ONE device.</summary>
    public sealed record MachineState
    {
        public State State { get; init; } = State.Unknown;
        public int ConsecutiveBad { get; init; }
        public DateTime? FirstBadUtc { get; init; }
        public bool Alerted { get; init; }
    }

    /// <summary>What the monitor observed for one device this tick.</summary>
    public sealed record Observation
    {
        public bool TailscaleUp { get; init; }
        /// <summary>The device's baseline; null (or non-LAN-direct) self-gates the machine to Unknown.</summary>
        public Baseline? Baseline { get; init; }
        public bool? CurrentDirect { get; init; }
        public bool CurrentIsLanPath { get; init; }
        /// <summary>Carried for context/logging only - the drift decision keys on PATH TYPE, never latency (P5 hardening: a direct-but-slow device is not drift). Latency lives in the dashboard trend.</summary>
        public double? CurrentLatencyMs { get; init; }
        /// <summary>
        /// The monitor's POSITIVE physical-presence verdict for a RELAYING device: a best-effort ARP probe
        /// to the device's cached 192.168.x LAN IP that resolved to the SAME MAC captured while it was
        /// LAN-direct. True = the device is on the home LAN right now (so relaying = real drift); false =
        /// absent / probe failed / MAC mismatch (device left, or a different device now holds that IP) =>
        /// never alert. Only consulted for the primary relay signal; the secondary latency signal already
        /// implies the device IS LAN-direct, so presence is self-evident there.
        /// </summary>
        public bool HomeLanPresent { get; init; }
        public DateTime NowUtc { get; init; }
    }

    public sealed record Decision
    {
        public MachineState Next { get; init; } = new();
        /// <summary>Rising edge into Drifted - fire the alert once per episode (P5 wires the channels).</summary>
        public bool ShouldAlert { get; init; }
        /// <summary>Falling edge out of an alerted Drifted state - emit the one resolution note.</summary>
        public bool ShouldResolve { get; init; }
        /// <summary>"unknown" | "ok" | "suspect" | "drifted".</summary>
        public string Status { get; init; } = "unknown";
    }

    /// <summary>The pure state transition. See the class summary for the discriminator rationale.</summary>
    public static Decision Decide(Observation obs, MachineState state)
    {
        // Self-gate to UNKNOWN when there is nothing to judge: Tailscale down, or no established LAN-direct
        // baseline (warmup, or a device that is normally away). Never drift, never alert. A prior alerted
        // episode resolves on the way into Unknown.
        if (!obs.TailscaleUp || obs.Baseline is not { IsLanDirect: true })
            return Settle(State.Unknown, "unknown", state);

        // Drift is RELAY by construction (Architect P5-hardening): a device whose baseline is LAN-direct now
        // shows a relay / not-direct path. Latency degradation on a STILL-DIRECT device is deliberately NOT
        // drift - a relay-framed alert (owner email AND the NetworkDrift doorbell) for a direct device is a
        // MISLEADING diagnosis, wrong cause + wrong fix, which for this mission is as bad as a false one. So
        // the machine keys ONLY on path type, never latency; "direct but slow" is shown honestly on the
        // dashboard's latency trend, where the framing is right, and never triggers a relay alert.
        bool relaying = obs.CurrentDirect == false || !obs.CurrentIsLanPath;

        if (!relaying)
            return Settle(State.Ok, "ok", state);

        // The crux of the "never a false alert" guarantee. A relaying device that "fell to DERP at home"
        // (DRIFT) and one whose "user left the house" (away, NOT drift) look IDENTICAL on the active path,
        // and tailscale exposes no persistent LAN candidate to tell them apart. No time window can close
        // this (a departed device's last-seen-home time is frozen at departure). So we require the monitor's
        // POSITIVE physical-presence verdict - a best-effort ARP probe to the device's cached LAN IP that
        // resolves to its SAME cached MAC. Absent / mismatch / probe error => UNKNOWN, never alert: we
        // deliberately MISS a home-drift over ever crying wolf, and the in-app pill catches the missed case
        // the moment the user looks.
        if (!obs.HomeLanPresent)
            return Settle(State.Unknown, "unknown", state);

        // Bad observation: accrue consecutive count + episode start.
        int consecutive = state.ConsecutiveBad + 1;
        DateTime firstBad = state.FirstBadUtc ?? obs.NowUtc;
        bool longEnough = (obs.NowUtc - firstBad) >= MinDriftDuration;

        if (consecutive >= ConsecutiveBadToDrift && longEnough)
        {
            bool rising = !state.Alerted; // fire exactly once per episode
            return new Decision
            {
                Next = new MachineState { State = State.Drifted, ConsecutiveBad = consecutive, FirstBadUtc = firstBad, Alerted = true },
                ShouldAlert = rising,
                Status = "drifted",
            };
        }

        return new Decision
        {
            Next = new MachineState { State = State.Suspect, ConsecutiveBad = consecutive, FirstBadUtc = firstBad, Alerted = state.Alerted },
            Status = "suspect",
        };
    }

    // Move to a settled good/unknown state. A resolution note fires ONLY on Drifted -> Ok (an OBSERVED
    // recovery to a direct path). Drifted -> Unknown (Tailscale down, or we lost the ability to judge)
    // must NOT resolve: going quiet is not the same as "fixed", and a false "your network recovered"
    // all-clear when nothing recovered is its own cry-wolf. We only ever claim recovery on seeing direct
    // restored. (Architect review: never a false all-clear.)
    private static Decision Settle(State next, string status, MachineState prior) => new()
    {
        Next = new MachineState { State = next },
        ShouldResolve = next == State.Ok && prior.State == State.Drifted && prior.Alerted,
        Status = status,
    };

    private static double Median(List<double> values)
    {
        values.Sort();
        int m = values.Count / 2;
        return values.Count % 2 == 0 ? (values[m - 1] + values[m]) / 2 : values[m];
    }
}
