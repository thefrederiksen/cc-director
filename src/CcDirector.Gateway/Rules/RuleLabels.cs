namespace CcDirector.Gateway.Rules;

/// <summary>
/// THE FINISHED LABELS A CLIENT RENDERS VERBATIM (fix round D, ruling D8; repository rule 7).
///
/// Both clients were composing "every session" and "10 minutes" for themselves, from different code -
/// the Cockpit in its rules client and again in the page, the command line in its own words - so the two
/// could disagree about the same stored state. A client never decides what a state MEANS; the Gateway
/// stamps the finished string onto the rule it serves, and adding a state is one edit here rather than
/// a new branch in every client.
/// </summary>
internal static class RuleLabels
{
    /// <summary>
    /// Which sessions a rule acts on, in the words a person reads. Every session is the honest answer
    /// when no part is set - because a scope with no part set IS every session, said out loud.
    ///
    /// AN ABSENT SCOPE IS A FAULT, NOT A LABEL (fix round F, ruling F3). This used to answer "every
    /// session" for a null scope as well, which is the same habit the store, the wire reader and both
    /// clients each had to have taken out of them: an absent value becoming the widest one it could
    /// mean. A client renders this string verbatim, so that default would have put the widest sentence
    /// there is in front of a person on the strength of a scope nobody ever said.
    /// </summary>
    /// <exception cref="ArgumentNullException">There is no scope. Nothing labels one.</exception>
    internal static string Scope(RuleScope scope)
    {
        if (scope is null) throw new ArgumentNullException(nameof(scope));
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(scope.Agent)) parts.Add("agent " + scope.Agent);
        if (!string.IsNullOrWhiteSpace(scope.Repository)) parts.Add("repository " + scope.Repository);
        if (!string.IsNullOrWhiteSpace(scope.Machine)) parts.Add("machine " + scope.Machine);
        if (!string.IsNullOrWhiteSpace(scope.Mission)) parts.Add("mission " + scope.Mission);
        return parts.Count == 0 ? "every session" : string.Join(", ", parts);
    }

    /// <summary>A wait in the words a person uses for it: whole hours, else whole minutes, else seconds.</summary>
    internal static string Wait(int seconds)
    {
        if (seconds >= 3600 && seconds % 3600 == 0)
            return (seconds / 3600) + (seconds == 3600 ? " hour" : " hours");
        if (seconds >= 60 && seconds % 60 == 0)
            return (seconds / 60) + (seconds == 60 ? " minute" : " minutes");
        return seconds + (seconds == 1 ? " second" : " seconds");
    }
}
