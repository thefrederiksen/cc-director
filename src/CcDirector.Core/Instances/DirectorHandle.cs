using CcDirector.Core.Utilities;

namespace CcDirector.Core.Instances;

/// <summary>
/// How a Director is NAMED - the one place that decides which handles identify a Director, what the
/// toolbar shows, and what its Copy button puts on the clipboard.
///
/// One machine runs several named Director instances, so the machine name cannot identify one: it says
/// which computer, not which Director. Two handles do, and they are the two a caller can actually be
/// given - the Director id (immutable, unambiguous, survives a rename and a restart) and the display
/// name (editable, the label the owner reads in the menu and the Fleet Map).
///
/// WHY ONE CLASS AND NOT THREE. The toolbar's Copy button hands another agent a spawn command, the
/// Director floor decides whether a named target is ITSELF, and the Gateway resolves the same name
/// against its registry. Those three have to agree on what a handle is: if the button emits a handle
/// the resolver does not accept, the paste fails on the far side, where nobody is watching. They agree
/// here or they drift.
///
/// The Gateway's own resolve (<c>RegistryDirectorTargetResolver</c>) matches the same two fields
/// against its registry rows, which is the same rule applied to rows this process cannot see.
/// </summary>
public static class DirectorHandle
{
    /// <summary>
    /// Does <paramref name="token"/> match this Director's ID? Case-insensitive; a blank token matches
    /// nothing (it is the ABSENCE of a target, and treating it as a match would silently claim every
    /// untargeted spawn).
    /// </summary>
    public static bool MatchesId(string? token, string? directorId)
        => token?.Trim() is { Length: > 0 } wanted && Same(wanted, directorId);

    /// <summary>
    /// Does <paramref name="token"/> match this Director's DISPLAY NAME? Same blank rule. Callers must
    /// not use this without first ruling out an id match - see <see cref="Pick{T}"/> for why.
    /// </summary>
    public static bool MatchesDisplayName(string? token, string? displayName)
        => token?.Trim() is { Length: > 0 } wanted && Same(wanted, displayName);

    /// <summary>
    /// The Directors that <paramref name="token"/> names, with ID PRECEDENCE: if any candidate's id
    /// matches exactly, that is the answer and display names are never consulted. Only when no id
    /// matches does the token fall through to display names, which may legitimately return several.
    ///
    /// WHY PRECEDENCE, NOT "EITHER". A display name is unrestricted text the owner types, so one
    /// Director can be named the literal id of another - by accident or on purpose. Matching both at
    /// equal rank then breaks the one guarantee the id exists to provide: a request carrying A's id
    /// would come back ambiguous, or resolve to B. An id is issued by the system and unique in the
    /// fleet; a name is not, so the id decides first and alone.
    ///
    /// Returns every match so the caller can tell "none" from "several" and report each loudly - this
    /// deliberately does not pick one, because picking among genuine duplicates is the guess that lands
    /// a session on a Director nobody chose.
    /// </summary>
    public static List<T> Pick<T>(IEnumerable<T> candidates, string? token,
        Func<T, string?> idOf, Func<T, string?> displayNameOf)
    {
        var all = candidates.ToList();
        var byId = all.Where(c => MatchesId(token, idOf(c))).ToList();
        var picked = byId.Count > 0
            ? byId
            : all.Where(c => MatchesDisplayName(token, displayNameOf(c))).ToList();

        // The one method here that DECIDES something, so the one that is logged: which token was
        // resolved, against how many candidates, to how many matches, and on which handle. That is
        // exactly what a reader needs when a spawn lands somewhere unexpected. The four methods above
        // are pure predicates and formatters with no failure mode of their own - logging each call
        // would bury this line under one entry per candidate per resolve and tell a reader nothing the
        // outcome here does not already say.
        FileLog.Write($"[DirectorHandle] Pick: token='{token}', candidates={all.Count}, " +
                      $"matched={picked.Count} by {(byId.Count > 0 ? "id" : "display name")}");
        return picked;
    }

    /// <summary>
    /// What the toolbar shows: the Director's own name. Falls back to the machine name when the
    /// instance has no display name, because a blank label identifies nothing at all - and never to
    /// the id, which no owner recognises on sight.
    ///
    /// The Control API PORT was here for years and is deliberately gone: it was the address another
    /// agent dialled, nothing dials a Director by port any more (the fleet is reached through the
    /// Gateway), and a number nobody uses reads as identity while telling you nothing about which of
    /// this machine's Directors you are looking at.
    /// </summary>
    public static string Label(string? displayName, string? machineName)
    {
        var name = displayName?.Trim();
        if (!string.IsNullOrEmpty(name))
            return name;

        var machine = machineName?.Trim();
        return string.IsNullOrEmpty(machine) ? "Director" : machine;
    }

    /// <summary>
    /// The clipboard text: WHO this Director is, so another agent can be told to reach it. Three
    /// labelled facts and nothing else - the name a person reads, the id anything addressing it should
    /// carry, and the machine it runs on.
    ///
    /// DELIBERATELY NOT A COMMAND. What the recipient should DO with this Director is the pasting
    /// person's instruction, not ours: they may want a session opened, a question asked, or nothing at
    /// all. A command baked in here would also have to invent the one thing this Director cannot know -
    /// which repository is meant - so it could never be run as pasted anyway. The same shape as the
    /// per-session copy, which likewise states facts and leaves the instruction to the human.
    /// </summary>
    public static string Identity(string? displayName, string? directorId, string? machineName)
    {
        var machine = machineName?.Trim();

        return $"Director: {Label(displayName, machineName)}\n"
             + $"Director ID: {directorId?.Trim() ?? ""}\n"
             + $"Machine: {(string.IsNullOrEmpty(machine) ? "(unknown)" : machine)}";
    }

    private static bool Same(string wanted, string? candidate)
        => !string.IsNullOrWhiteSpace(candidate)
           && string.Equals(wanted, candidate.Trim(), StringComparison.OrdinalIgnoreCase);
}
