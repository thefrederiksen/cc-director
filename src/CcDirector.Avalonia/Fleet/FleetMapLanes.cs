using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Fleet;

/// <summary>How the desktop fleet map slices the fleet. Mirrors the Cockpit's pivots (issue #1627).</summary>
public enum FleetPivot
{
    /// <summary>Lane = repository. What we are building.</summary>
    Repository,

    /// <summary>Lane = agent (ClaudeCode / Codex / ...). The workforce.</summary>
    Agent,
}

/// <summary>One lane: a header plus the sessions under it, already in tree render order.</summary>
public sealed class FleetLane
{
    public required string Title { get; init; }
    public required List<FleetTreeNode> Nodes { get; init; }

    /// <summary>Live session count in this lane (the header's subtitle).</summary>
    public int Count => Nodes.Count;
}

/// <summary>
/// Issue #1627: group the fleet roster into the lanes the desktop fleet map draws, then order each lane as
/// the spawn tree. Pure, so the grouping rules are unit tested rather than only ever exercised by eye.
/// </summary>
public static class FleetMapLanes
{
    /// <summary>
    /// Issue #1627: narrow the roster to what the map should show.
    ///
    /// When <paramref name="showWholeFleet"/> is false - the default - only the sessions THIS Director runs
    /// survive. That is not an arbitrary default: they are exactly the sessions a click here can open in
    /// the rail. Everything else has to go out to the Cockpit, which is a different experience, so seeing
    /// it is opt-in.
    ///
    /// The filter is by DIRECTOR, not by machine, and the distinction is real: a machine can run several
    /// Directors, and this Director cannot open another one's sessions even though they sit on the same
    /// box. Filtering by Director is what makes "visible" and "clickable" mean the same thing here, with
    /// no exceptions to explain.
    /// </summary>
    public static List<SessionDto> Filter(IReadOnlyList<SessionDto> sessions, HashSet<string> localSessionIds, bool showWholeFleet)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(localSessionIds);
        if (showWholeFleet) return sessions.ToList();
        return sessions.Where(s => localSessionIds.Contains(s.SessionId ?? "")).ToList();
    }

    /// <summary>The lane key for one session under a pivot. Blank values fall into a named bucket rather
    /// than vanishing - a session with no repository is a fact worth seeing, not a card to drop.</summary>
    public static string LaneKey(SessionDto s, FleetPivot pivot)
    {
        if (pivot == FleetPivot.Agent)
        {
            var agent = (s.Agent ?? "").Trim();
            return agent.Length == 0 ? "(unknown agent)" : agent;
        }
        var repo = RepoBasename(s.RepoPath);
        return repo.Length == 0 ? "(no repository)" : repo;
    }

    /// <summary>The last path segment of a repository path, which is how a repo is named everywhere else.</summary>
    public static string RepoBasename(string? repoPath)
    {
        var p = (repoPath ?? "").Trim().TrimEnd('\\', '/');
        if (p.Length == 0) return "";
        var idx = p.LastIndexOfAny(new[] { '\\', '/' });
        return idx >= 0 ? p[(idx + 1)..] : p;
    }

    /// <summary>
    /// Build the lanes. Lanes are ordered by name so the map does not reshuffle between polls - the same
    /// reason the Cockpit fixes lane order before filtering. Within a lane, cards are the spawn tree.
    /// </summary>
    public static List<FleetLane> Build(IReadOnlyList<SessionDto> sessions, FleetPivot pivot, Comparison<SessionDto> sort)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sort);

        var byLane = new Dictionary<string, List<SessionDto>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sessions)
        {
            var key = LaneKey(s, pivot);
            if (!byLane.TryGetValue(key, out var arr))
                byLane[key] = arr = new List<SessionDto>();
            arr.Add(s);
        }

        var lanes = new List<FleetLane>();
        foreach (var kv in byLane.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            lanes.Add(new FleetLane { Title = kv.Key, Nodes = FleetMapTree.Build(kv.Value, sort) });

        return lanes;
    }

    /// <summary>
    /// The default card order: by session number when both have one (the identity the owner reads), then
    /// by name. Stable, so a card keeps its slot between polls.
    /// </summary>
    public static int DefaultSort(SessionDto a, SessionDto b)
    {
        var an = a.Number;
        var bn = b.Number;
        if (an.HasValue && bn.HasValue && an.Value != bn.Value) return an.Value.CompareTo(bn.Value);
        if (an.HasValue && !bn.HasValue) return -1;
        if (!an.HasValue && bn.HasValue) return 1;
        return string.Compare(a.Name ?? "", b.Name ?? "", StringComparison.OrdinalIgnoreCase);
    }
}
