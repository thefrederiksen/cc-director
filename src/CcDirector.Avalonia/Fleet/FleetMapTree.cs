using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Fleet;

/// <summary>
/// One card in a lane, flattened out of the controller tree with the indent level it renders at.
/// </summary>
public sealed class FleetTreeNode
{
    public required SessionDto Session { get; init; }

    /// <summary>0 for a root card; 1 for a child of a root, and so on. Not capped - nesting is real.</summary>
    public required int Depth { get; init; }
}

/// <summary>
/// Issue #1627: order a lane's sessions as the spawn tree, so a Manager's Workers sit under it instead of
/// scattered through the lane. This is the desktop's rendering of the SAME tree the Cockpit's Fleet Map
/// renders (issue #1626), from the SAME wire facts.
///
/// WHAT THIS DOES NOT DO. It does not resolve roles. The role is a GATEWAY-owned fact
/// (<see cref="SessionDto.SessionRole"/>, computed by the Gateway's FleetRoleResolver) and is READ here,
/// never recomputed: "is this session's controller still alive?" is unanswerable from one Director,
/// because the controller may be a session on another machine entirely. Note in particular that
/// <c>SessionManager.ResolveLocalRole</c> exists on this same Director and mirrors that logic against the
/// LOCAL roster only - it is a best-effort rail glyph, it is wrong for exactly the cross-machine case, and
/// it must NOT be used here. This class decides ORDER and INDENT only, which are presentation.
///
/// The rules are the Cockpit's rules, deliberately identical, because the two views showing the same fleet
/// differently would be worse than either. They are restated (not shared) because the Cockpit's copy is
/// TypeScript in the browser; there is no code path that could carry one implementation to both. The ROLE
/// - the thing that must not drift - is not restated: it arrives on the wire already decided.
/// </summary>
public static class FleetMapTree
{
    /// <summary>
    /// Flatten <paramref name="sessions"/> into render order with an indent depth per card.
    ///
    /// Four rules, each of which is a case that actually occurs:
    ///
    ///  - A controller that is not in this lane is not a parent here. The pivots slice the fleet, so a
    ///    Worker's Manager can be filtered out (a different repository, a different agent). Such a child
    ///    renders at the lane's top level rather than under a parent the lane cannot show.
    ///  - An EXITED controller is not a parent. The Gateway already demotes a session whose controller has
    ///    exited back to Standalone; indenting it under the corpse would say the opposite of the roster.
    ///  - A cycle cannot hang the view. A session that cannot reach a root by walking controllers is
    ///    treated as a root itself.
    ///  - Every session renders exactly once. Cards are never dropped by this pass - a lost card is a worse
    ///    bug than a badly indented one.
    /// </summary>
    /// <param name="sessions">The lane's sessions.</param>
    /// <param name="sort">Sibling/root ordering. Applied to roots and to each parent's children.</param>
    public static List<FleetTreeNode> Build(IReadOnlyList<SessionDto> sessions, Comparison<SessionDto> sort)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(sort);

        var byId = new Dictionary<string, SessionDto>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            var id = (s.SessionId ?? "").Trim();
            if (id.Length > 0)
                byId[id] = s;
        }

        static bool IsAlive(SessionDto s)
            => !string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

        // The controller this session actually hangs under IN THIS LANE, or null when it is a root here.
        string? ParentOf(SessionDto s)
        {
            if (!s.IsControlled) return null;
            var cid = (s.ControllerSessionId ?? "").Trim();
            if (cid.Length == 0) return null;
            if (string.Equals(cid, (s.SessionId ?? "").Trim(), StringComparison.Ordinal)) return null; // its own root
            if (!byId.TryGetValue(cid, out var parent)) return null; // controller not in this lane
            if (!IsAlive(parent)) return null; // never indent under a corpse
            return cid;
        }

        // Walk up to a root to prove this session is reachable. A session in a cycle never reaches one, so
        // it is promoted to a root rather than being lost or looping forever.
        bool ReachesRoot(SessionDto s)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var cur = s;
            while (true)
            {
                var id = (cur.SessionId ?? "").Trim();
                if (!seen.Add(id)) return false;
                var pid = ParentOf(cur);
                if (pid is null) return true;
                if (!byId.TryGetValue(pid, out var next)) return true;
                cur = next;
            }
        }

        var roots = new List<SessionDto>();
        var childrenOf = new Dictionary<string, List<SessionDto>>(StringComparer.Ordinal);
        foreach (var s in sessions)
        {
            var pid = ParentOf(s);
            if (pid is null || !ReachesRoot(s))
            {
                roots.Add(s);
                continue;
            }
            if (!childrenOf.TryGetValue(pid, out var arr))
                childrenOf[pid] = arr = new List<SessionDto>();
            arr.Add(s);
        }

        roots.Sort(sort);

        var outp = new List<FleetTreeNode>();
        var emitted = new HashSet<string>(StringComparer.Ordinal);

        void Walk(SessionDto s, int depth)
        {
            var id = (s.SessionId ?? "").Trim();
            if (id.Length > 0 && !emitted.Add(id)) return;
            outp.Add(new FleetTreeNode { Session = s, Depth = depth });
            if (!childrenOf.TryGetValue(id, out var kids)) return;
            var ordered = new List<SessionDto>(kids);
            ordered.Sort(sort);
            foreach (var k in ordered) Walk(k, depth + 1);
        }

        foreach (var r in roots) Walk(r, 0);
        return outp;
    }
}
