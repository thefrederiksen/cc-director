namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Issue #1051: turn per-Director reachability into a COMPLETENESS VERDICT, in words a client prints
/// verbatim.
///
/// The defect this exists for: the Gateway drops an unreachable Director's sessions from the roster and
/// still answers 200, so a caller cannot tell "that Director has no sessions" from "I could not reach that
/// Director". Absent reads identical to empty. Because the roster is what every command line verb resolves
/// a target against, a session on an unreachable machine is unnameable - the same failure #1019 fixed for
/// a crashed session, one machine over.
///
/// It lives HERE, beside <see cref="SessionOrdering"/>, for the same reason that does: deciding what a
/// state MEANS is the single fold, and a client that rules for itself renders something plausible the
/// moment it meets a state it did not expect. Three command line tools resolve against this roster, and
/// three copies of this judgement would drift; one cannot.
/// </summary>
public static class RosterCompleteness
{
    /// <summary>
    /// Whether the roster is the whole fleet, and if not, why - as a finished sentence.
    ///
    /// ONLY <see cref="DirectorReachabilityDto.StateOffline"/> counts as incomplete. A
    /// <see cref="DirectorReachabilityDto.StateWobbly"/> Director is inside the grace window and its machine
    /// is still reachable, so the roster is whole and merely part-stale. Reporting wobbly as incomplete would
    /// put a caveat on the most frequently run command in the tool for a case where nothing is doubtful -
    /// which teaches the reader to skip it, and then it is not read on the day offline finally happens.
    ///
    /// WHAT "INCOMPLETE" MEANS HERE CHANGED with epic #1159 step A, and the wording had to change with it
    /// (inspection 1, finding 4). It used to mean an offline Director's sessions had been DROPPED from the
    /// response, and the sentence said so. The roster now serves that machine's last-known rows instead of
    /// deleting them, so the old sentence was handed to every command line caller alongside the very rows it
    /// claimed were missing - a contract stating an implementation that no longer exists.
    ///
    /// The caution is still worth printing, for a different and still-true reason: an offline Director did
    /// not answer, so what is listed for it is the last thing it said and not a confirmed present state.
    /// Sessions may have started or ended since. That is a caveat about AUTHORITY, not about absence.
    ///
    /// An empty or absent list is COMPLETE, not unknown: that is the standalone floor, where there is no
    /// Gateway and no other Director that could be hiding anything.
    /// </summary>
    public static (bool Complete, string? Reason) Fold(IReadOnlyList<DirectorReachabilityDto>? reachability)
    {
        if (reachability is null || reachability.Count == 0)
            return (true, null);

        var offline = reachability
            .Where(r => string.Equals(r.State, DirectorReachabilityDto.StateOffline, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (offline.Count == 0)
            return (true, null);

        var named = offline.Select(r =>
        {
            var who = !string.IsNullOrWhiteSpace(r.MachineName) ? r.MachineName : r.DirectorId;
            if (string.IsNullOrWhiteSpace(who))
                who = "an unidentified Director";
            // "never reached" and "last seen a while ago" are different facts, and inventing an age for
            // the first would be a fabricated number. Say which one it is.
            var age = r.LastSeenAgeSeconds is double secs and > 0
                ? $", last seen {DescribeAge(secs)} ago"
                : r.LastSeenUtc is null ? ", never reached" : "";
            var why = !string.IsNullOrWhiteSpace(r.Error) ? $": {r.Error}" : "";
            return $"{who}{age}{why}";
        });

        var count = offline.Count == 1
            ? "1 Director could not be reached, so its sessions below are the last it reported and may be out of date"
            : $"{offline.Count} Directors could not be reached, so their sessions below are the last they reported and may be out of date";
        return (false, $"{count} - {string.Join("; ", named)}");
    }

    /// <summary>A coarse, human age for a last-seen gap. Deliberately vague - the precision is not the point.</summary>
    public static string DescribeAge(double seconds)
    {
        if (seconds < 90) return $"{Math.Round(seconds)}s";
        if (seconds < 5400) return $"{Math.Round(seconds / 60)}m";
        return $"{Math.Round(seconds / 3600)}h";
    }
}
