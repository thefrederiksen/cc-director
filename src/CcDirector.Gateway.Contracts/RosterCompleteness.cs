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

    /// <summary>
    /// The caution to print when a lookup came back with NOTHING and a connected-but-quiet machine could be
    /// the reason. Null when there is no such machine, which is almost always.
    ///
    /// WHY THIS IS SEPARATE FROM <see cref="Fold"/>, and why it is scoped to a negative answer. The two
    /// inspections of this branch reached opposite conclusions and BOTH were right. The first said a blanket
    /// caution on the most-run command in the tool teaches the reader to skip it, and then it is not read on
    /// the day it matters. The second said calling a connected-but-stale roster COMPLETE is simply false: a
    /// wobbly Director is a tunnel that is up with no recent push, so its rows may omit a session started
    /// since, exactly the authority problem the offline wording now describes.
    ///
    /// Both hold, so neither extreme is taken. The caution is raised only where the staleness can actually
    /// change the answer: when the caller asked for something and got NOTHING. A lookup that found what it
    /// wanted needs no caveat - the stale set did not hide it, because it produced it. A lookup that came
    /// back empty while a machine's rows are known to be stale needs one, because that is the only case
    /// where "not found" and "not visible from here" differ. Rare enough to still be read.
    ///
    /// <see cref="Fold"/> keeps its own, separate caution for OFFLINE Directors, which is about the
    /// authority of rows that ARE shown and therefore applies to a positive answer too. Different claims,
    /// different triggers.
    /// </summary>
    public static string? StaleAnswerCaution(IReadOnlyList<DirectorReachabilityDto>? reachability)
    {
        if (reachability is null || reachability.Count == 0)
            return null;

        var stale = reachability
            .Where(r => string.Equals(r.State, DirectorReachabilityDto.StateWobbly, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (stale.Count == 0)
            return null;

        var named = stale.Select(r =>
        {
            var who = !string.IsNullOrWhiteSpace(r.MachineName) ? r.MachineName : r.DirectorId;
            if (string.IsNullOrWhiteSpace(who))
                who = "an unidentified Director";
            return r.LastSeenAgeSeconds is double secs and > 0 ? $"{who}, last reported {DescribeAge(secs)} ago" : who;
        });

        var count = stale.Count == 1
            ? "1 machine is connected but has not reported recently"
            : $"{stale.Count} machines are connected but have not reported recently";
        return $"{count}, so something that started there may not be in this list yet - {string.Join("; ", named)}";
    }

    /// <summary>
    /// A coarse, human age for a last-seen gap. Deliberately vague - the precision is not the point.
    ///
    /// THE RULE IS THE CLIENTS' RULE, deliberately: truncate, and change unit at 60 seconds and at one hour.
    /// It matches <c>reachabilityLastSeen</c> in client-core exactly, because the two now appear ON THE SAME
    /// SCREEN - the Fleet Map's warning line is written here, and the age beside the very Director it names is
    /// written there. They used to differ in both rounding and thresholds, so one Director could read
    /// "last seen 32m ago" in the banner and "last seen 31m ago" in its own lane, one line apart. An age that
    /// contradicts itself in two places is worse than a vague one: the reader stops trusting either.
    ///
    /// Two languages means two copies of the rule; there is no third. Keep them equal - the paired cases in
    /// RosterCompletenessFoldTests and fleetClient's own tests pin the values where they used to diverge.
    /// </summary>
    public static string DescribeAge(double seconds)
    {
        // AwayFromZero, not the .NET default. C# rounds midpoints to even and JavaScript rounds them up, so
        // the default would put 58.5 seconds at "58s" here and "59s" in the client - one line apart on the
        // same screen, for the same Director. A half-second is a trivial quantity and a contradiction is not.
        if (seconds < 60) return $"{Math.Round(seconds, MidpointRounding.AwayFromZero)}s";
        if (seconds < 3600) return $"{Math.Floor(seconds / 60)}m";
        return $"{Math.Floor(seconds / 3600)}h";
    }
}
