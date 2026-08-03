namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE fold that turns per-Director reachability into what a client prints: each Director's own badge,
/// placeholder and available action, and the fleet-wide banner above the map.
///
/// It exists because both of those verdicts were being made in the view. The Fleet Map counted the rows in
/// <c>machineErrors</c> - a PER-DIRECTOR list, one row per Director the Gateway could not reach - and printed
/// them with the word "machine". A machine carrying three Directors and fifteen live sessions, one of whose
/// slots had been shut down, therefore announced "1 machine unreachable on the last sweep: SOREN_NORTH" while
/// every session on it was pushing every few seconds. The count was a Director count, the noun was a machine,
/// and the reader had no way to tell that the machine was fine.
///
/// So the altitude is decided here, once: A MACHINE IS UNREACHABLE ONLY WHEN EVERY DIRECTOR ON IT IS. One
/// unreachable slot among several is a Director-level fact and is reported as one, naming the slot. This is
/// the same reasoning that puts <see cref="SessionOrdering"/> and <see cref="RosterCompleteness"/> here rather
/// than in a client: deciding what a state MEANS is one fold, and a view that rules for itself renders
/// something plausible the moment it meets a state it did not expect.
/// </summary>
public static class FleetReachabilityFold
{
    /// <summary>
    /// Stamp one Director's presentation fields from its state. Every client-visible judgement about what the
    /// state MEANS is made here: the badge word, whether its rows are last-known, whether a session could be
    /// started on it, and the line to show when it has no sessions.
    /// </summary>
    public static void Describe(DirectorReachabilityDto d)
    {
        if (d is null) return;

        if (Is(d.State, DirectorReachabilityDto.StateWobbly))
        {
            d.StateLabel = "Wobbly";
            d.DataIsStale = true;
            // The tunnel is UP - only the last push is late - so a start still lands. Withholding the action
            // here would make the button flicker away every time a push ran a few seconds behind.
            d.CanStartSession = true;
            d.EmptySlotText = "No sessions - free slot";
            return;
        }

        if (Is(d.State, DirectorReachabilityDto.StateOffline))
        {
            d.StateLabel = "Offline";
            d.DataIsStale = true;
            d.CanStartSession = false;
            // "This director", not "this machine": the machine may be perfectly healthy and running other
            // Directors, which is the mislabel this whole fold exists to stop repeating.
            d.EmptySlotText = "No sessions - this director cannot be reached";
            return;
        }

        if (Is(d.State, DirectorReachabilityDto.StateStopped))
        {
            d.StateLabel = "Not running";
            // Its rows are still last-known rather than confirmed - a clean shutdown removes its sessions, so
            // there are normally none, but anything left over must not read as live.
            d.DataIsStale = true;
            // NOT free capacity, however ordinary the state is: the tunnel a start would travel down is gone
            // with the process, so the action could not be honoured.
            d.CanStartSession = false;
            d.EmptySlotText = "No sessions - this director is not running";
            return;
        }

        // Online, and anything unrecognised. An unknown state falls here on purpose: a healthy-looking render
        // with no badge is the safe degradation, and the client never has to invent one.
        d.StateLabel = "";
        d.DataIsStale = false;
        d.CanStartSession = true;
        d.EmptySlotText = "No sessions - free slot";
    }

    /// <summary>
    /// The warning line above the Fleet Map, as a finished sentence, or null when there is nothing wrong.
    ///
    /// ONLY <see cref="DirectorReachabilityDto.StateOffline"/> is a problem. A wobbly Director is answering,
    /// just late. A STOPPED one was shut down on purpose and is exactly where it should be - counting that as
    /// a fault is what made an ordinary shutdown look like an outage for a day.
    ///
    /// An offline Director is then reported at the altitude the evidence supports: as a dead MACHINE when
    /// every Director on that machine is offline, and as a dead SLOT when the machine still has a Director
    /// answering. Both can be true of one fleet at once, so both clauses can appear.
    /// </summary>
    public static string? UnreachableBanner(IReadOnlyList<DirectorReachabilityDto>? reachability)
    {
        if (reachability is null || reachability.Count == 0) return null;

        var offline = reachability.Where(r => r is not null && Is(r.State, DirectorReachabilityDto.StateOffline)).ToList();
        if (offline.Count == 0) return null;

        // Group the WHOLE list by machine, not just the offline rows: the question is whether anything else on
        // that machine is still answering, and only the full list can answer it.
        var byMachine = reachability
            .Where(r => r is not null)
            .GroupBy(r => MachineKey(r.MachineName), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var deadMachines = new List<string>();
        // The machine names already spoken for, tracked SEPARATELY from the rendered entries above. Testing
        // the rendered list cannot work: each entry carries its own age suffix, so two Directors on one dead
        // machine produce two different strings and neither matches the other - which put the per-Director
        // count straight back under a machine noun, the exact defect this fold exists to remove.
        var namedMachines = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deadSlots = new List<string>();
        foreach (var r in offline)
        {
            var siblings = byMachine.TryGetValue(MachineKey(r.MachineName), out var s) ? s : new List<DirectorReachabilityDto> { r };
            // THE QUESTION IS WHETHER ANYTHING ON THAT MACHINE IS STILL ANSWERING, and a STOPPED Director
            // answers neither way. It is not evidence the machine is alive (nobody is there) and it is not
            // evidence the machine is dead (it left on purpose), so it is excluded from the judgement
            // entirely rather than counted on either side.
            //
            // Counting it as "not offline" was wrong in a way that produced a provably false sentence: a
            // machine with one stopped slot and one genuinely unreachable Director failed the whole-machine
            // test, so it was reported as a lone dead director followed by "the rest of that machine is
            // answering normally" - when nothing on it was answering at all.
            var answering = siblings.Any(x => !Is(x.State, DirectorReachabilityDto.StateOffline)
                                              && !Is(x.State, DirectorReachabilityDto.StateStopped));
            // A machine with no name cannot be judged as a machine at all - it cannot be grouped with
            // confidence - so it is always reported as the single Director it is.
            var wholeMachineIsDown = !string.IsNullOrWhiteSpace(r.MachineName) && !answering;
            if (wholeMachineIsDown)
            {
                var name = r.MachineName.Trim();
                // One entry per MACHINE, however many Directors it lost - the count and the noun have to agree,
                // which is the defect this replaced.
                //
                // The age is the MOST RECENTLY heard of that machine's Directors, not whichever one this loop
                // reached first. The claim being made is about the machine, and the machine was last heard when
                // the last of its Directors was; quoting an older Director would overstate how long it has been
                // gone, on a line whose whole job is to say how long it has been gone.
                if (namedMachines.Add(name))
                {
                    var freshest = siblings
                        .Where(x => x.LastSeenAgeSeconds is double)
                        .OrderBy(x => x.LastSeenAgeSeconds!.Value)
                        .FirstOrDefault() ?? r;
                    deadMachines.Add($"{name}{AgeSuffix(freshest)}");
                }
            }
            else
            {
                deadSlots.Add($"{SlotName(r)}{AgeSuffix(r)}");
            }
        }

        var clauses = new List<string>();
        if (deadMachines.Count > 0)
        {
            var head = deadMachines.Count == 1
                ? "1 machine could not be reached on the last sweep"
                : $"{deadMachines.Count} machines could not be reached on the last sweep";
            clauses.Add($"{head}: {string.Join("; ", deadMachines)}");
        }
        if (deadSlots.Count > 0)
        {
            // "on the last sweep" is said once per sentence, by whichever clause comes first - it dates the
            // whole reading, and repeating it in both clauses reads like two separate reports.
            var when = clauses.Count == 0 ? " on the last sweep" : "";
            var head = deadSlots.Count == 1
                ? $"1 director could not be reached{when}"
                : $"{deadSlots.Count} directors could not be reached{when}";
            // The reassurance is the correction itself, and it is the part the owner actually needs: the
            // machine is up, so nothing on it has stopped working.
            var tail = deadSlots.Count == 1
                ? " - the rest of that machine is answering normally"
                : " - the rest of those machines is answering normally";
            clauses.Add($"{head}: {string.Join("; ", deadSlots)}{tail}");
        }

        return string.Join(". ", clauses);
    }

    /// <summary>How one Director is named in the banner: its display name when it has one, else its short id,
    /// and the machine it sits on so the reader knows where to look.</summary>
    private static string SlotName(DirectorReachabilityDto r)
    {
        var name = (r.DisplayName ?? "").Trim();
        if (name.Length == 0)
        {
            var id = (r.DirectorId ?? "").Trim();
            name = id.Length == 0 ? "an unidentified director" : $"director {Short(id)}";
        }
        var machine = (r.MachineName ?? "").Trim();
        return machine.Length == 0 ? name : $"{name} on {machine}";
    }

    /// <summary>" (last seen 32m ago)" / " (never reached)" / "". Never invents an age it does not have -
    /// "never reached" and "last seen a while ago" are different facts.</summary>
    private static string AgeSuffix(DirectorReachabilityDto r)
    {
        if (r.LastSeenAgeSeconds is double secs and > 0)
            return $" (last seen {RosterCompleteness.DescribeAge(secs)} ago)";
        return r.LastSeenUtc is null ? " (never reached)" : "";
    }

    private static string MachineKey(string? machineName) => (machineName ?? "").Trim().ToLowerInvariant();

    private static string Short(string directorId)
    {
        var dash = directorId.LastIndexOf('-');
        var tail = dash >= 0 ? directorId[(dash + 1)..] : directorId;
        return tail.Length > 8 ? tail[..8] : tail;
    }

    private static bool Is(string? state, string expected) => string.Equals(state, expected, StringComparison.OrdinalIgnoreCase);
}
