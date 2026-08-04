using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE SKILLS THE GATEWAY SERVES MUST NOT SEND AGENTS TO THE DELETED DOOR.
///
/// This is the mission's founding reason inverted, and it is why this guard exists rather than a
/// one-file correction. The owner's stated fear was agents using the wrong door; independent
/// inspection found the product SHIPPING a document that sent them to a dead one - the built-in
/// move-session skill still told agents the Director loopback served <c>/healthz</c>, to select a
/// target by probing Director ports, and to set <c>CC_DIRECTOR_API</c> for spawn and buffer. Phase 6
/// believed it had fixed that skill; it had fixed the LAUNCHER references and left every Director one.
///
/// These files are ACTIVE PRODUCT SURFACE, not documentation. They are embedded in the Gateway
/// assembly and served to every agent that asks for a skill, so a stale instruction here is executed,
/// not merely read. That is what makes this worth a test: prose does not get compiled, so nothing else
/// in the build would ever notice.
///
/// WHAT IT BANS IS AN INSTRUCTION, NOT A WORD. A skill saying "there is no CC_DIRECTOR_API" is the
/// correct thing to say and must stay legal, so the patterns below match the SHAPE of a usable
/// address - a variable being assigned a URL, a loopback URL on a Director port, a route being
/// fetched - rather than the name of the thing being disclaimed. A guard that banned the word would
/// have been deleted the first time someone tried to write the truth.
/// </summary>
public sealed class BuiltInSkillsHaveNoDeadDoorTests
{
    /// <summary>
    /// Each pattern with the thing it would do to an agent that followed it. The message matters as
    /// much as the pattern: whoever hits this is editing prose and needs to know what to write instead.
    /// </summary>
    private static readonly (string Pattern, string Why)[] DeadDoorPatterns =
    {
        (@"CC_DIRECTOR_API\s*=",
            "sets CC_DIRECTOR_API to an address. The Director has no listener and nothing reads that "
            + "variable; use 'cc-devthrottle --director <id-or-name>' to name a target Director."),

        (@"https?://(127\.0\.0\.1|localhost)(:\d+)?/healthz",
            "fetches /healthz on a Director. That route was deleted with the listener - nothing answers "
            + "on any port. Liveness comes from 'cc-devthrottle director list'."),

        (@"\bprob(e|ing)\b[^.\n]{0,60}?(`?/healthz`?|\bports?\b)",
            "tells an agent to find a Director by probing. There is nothing to probe; a Director is "
            + "named by its identifier, from 'cc-devthrottle director list'. If you are DESCRIBING "
            + "this as withdrawn rather than instructing it, say so without restating the recipe - "
            + "prose that reads like the instruction is what the next agent will act on."),
    };

    public static TheoryData<string> ShippedSkillFiles
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var file in Directory.GetFiles(SkillContentDirectory(), "*.skill.md"))
                data.Add(Path.GetFileName(file));
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ShippedSkillFiles))]
    public void A_shipped_skill_never_instructs_an_agent_to_use_the_deleted_Director_listener(string fileName)
    {
        var path = Path.Combine(SkillContentDirectory(), fileName);
        var text = File.ReadAllText(path);

        var offences = new List<string>();
        foreach (var (pattern, why) in DeadDoorPatterns)
        {
            foreach (Match match in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
            {
                var line = text.Take(match.Index).Count(c => c == '\n') + 1;
                offences.Add($"line {line}: \"{match.Value}\" - {why}");
            }
        }

        Assert.True(offences.Count == 0,
            $"{fileName} is served to agents by the Gateway and {offences.Count} passage(s) send them to "
            + "the door the remove-the-network-port mission deleted:\n  " + string.Join("\n  ", offences));
    }

    /// <summary>
    /// DETECTOR VALIDATION. The patterns have to actually match the text they describe, or the theory
    /// above passes because it is looking for something that could never appear. Each is checked
    /// against the passage inspection 3 found in the real skill, verbatim.
    /// </summary>
    [Fact]
    public void The_patterns_match_the_passages_the_inspection_actually_found()
    {
        var realOffences = new[]
        {
            "CC_DIRECTOR_API=http://127.0.0.1:<targetPort> cc-devthrottle session spawn \"<repoPath>\"",
            "curl -s -H \"Authorization: Bearer <secret>\" http://127.0.0.1:7879/healthz",
            "override the API base with that Director's Control API port (found by probing /healthz on each candidate port",
        };

        foreach (var offence in realOffences)
        {
            Assert.True(
                DeadDoorPatterns.Any(p => Regex.IsMatch(offence, p.Pattern, RegexOptions.IgnoreCase)),
                $"no pattern matches a passage that really shipped: {offence}");
        }

        // And the correct prose - a skill SAYING the door is gone - must stay legal, or the guard would
        // forbid telling the truth and would be removed rather than obeyed.
        const string correct =
            "There is no port, no `/healthz`, no loopback address and no `CC_DIRECTOR_API` - the "
            + "listener was deleted and it is not coming back.";
        Assert.DoesNotMatch(
            new Regex(string.Join("|", DeadDoorPatterns.Select(p => p.Pattern)), RegexOptions.IgnoreCase),
            correct);
    }

    /// <summary>The embedded skill content, found from the test output by walking up to the solution
    /// file - never a hard-coded path, so this runs in any checkout and any worktree.</summary>
    private static string SkillContentDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CcDirector.Gateway", "Skills", "Content");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new InvalidOperationException(
            "could not locate the built-in skill content above " + AppContext.BaseDirectory);
    }
}
