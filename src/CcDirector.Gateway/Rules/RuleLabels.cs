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
    /// <summary>Which sessions a rule acts on, in the words a person reads. Every session is the honest
    /// answer when no part is set.</summary>
    internal static string Scope(RuleScope? scope)
    {
        if (scope is null) return "every session";
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
