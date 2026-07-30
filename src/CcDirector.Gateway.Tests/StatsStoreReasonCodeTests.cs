using CcDirector.Gateway.Stats.Data;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// EVERY REASON HAS A STABLE CODE, AND NO TWO SHARE ONE - checked MECHANICALLY, over the enum itself.
///
/// WHY THIS IS A TEST AND NOT A COMMENT. <c>GatewayStatsStore.CodeFor</c> used to carry a comment claiming
/// that adding a reason without a code "fails to compile". That was false. A C# switch expression over an
/// enum does not fail to compile on a missing member - it throws at RUN TIME, and on the path that matters
/// that throw lands inside the statistics boundary's own catch, which reports it as UNREACHABLE. So the
/// failure mode of a forgotten code is not a loud crash: it is somebody's DISK problem silently reported as
/// a NETWORK problem, which is exactly what the named reasons exist to prevent, arriving through the
/// mechanism meant to guarantee them.
///
/// AND IT HAS ALREADY HAPPENED, which is why this is not a hypothetical worry. Worker 2 added
/// <see cref="StatsStoreUnavailableReason.StoreSchemaIncomplete"/> and
/// its sibling on its own branch; worker 6's code
/// map, written before those existed, did not know them. Nothing failed to build. Nobody was told. Two
/// branches that were each individually correct produced a silent mis-naming the moment they met - which is
/// the ordinary way this class of defect arrives, and no amount of care by either author would have caught
/// it, because neither author could see the other's member.
///
/// The guard walks <c>Enum.GetValues</c> rather than a list written out here, so it cannot fall behind the
/// enum it is guarding. A list would be the same forgettable rule wearing a different hat.
/// </summary>
public sealed class StatsStoreReasonCodeTests
{
    private readonly ITestOutputHelper _out;

    public StatsStoreReasonCodeTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void EveryReason_HasACode_AndNoTwoReasonsShareOne()
    {
        var reasons = Enum.GetValues<StatsStoreUnavailableReason>();

        // The fixture's own premise: if the enum were empty or held one member, this test could not show a
        // collision and would pass while proving nothing.
        Assert.True(reasons.Length >= 2);

        var codes = new Dictionary<string, StatsStoreUnavailableReason>(StringComparer.Ordinal);
        var missing = new List<string>();
        var collisions = new List<string>();

        foreach (var reason in reasons)
        {
            string code;
            try
            {
                code = GatewayStatsStore.CodeFor(reason);
            }
            catch (ArgumentOutOfRangeException)
            {
                // Collected rather than thrown, so ONE run names EVERY member that is missing a code. A test
                // that stopped at the first would send somebody round this loop once per forgotten member.
                missing.Add(reason.ToString());
                continue;
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                missing.Add(reason.ToString());
                continue;
            }

            if (codes.TryGetValue(code, out var owner))
                collisions.Add($"{reason} and {owner} both answer to '{code}'");
            else
                codes[code] = reason;

            _out.WriteLine($"{reason} -> {code}");
        }

        Assert.True(
            missing.Count == 0,
            "These statistics unavailability reasons have NO stable code, so a surface cannot key off them " +
            "and the boundary would mis-report them as unreachable: " + string.Join(", ", missing));

        Assert.True(
            collisions.Count == 0,
            "Two reasons share one code, so an operator grepping for it cannot tell which fault they have: " +
            string.Join("; ", collisions));

        Assert.Equal(reasons.Length, codes.Count);
    }

    /// <summary>
    /// The codes are lower_snake_case and free of spaces. They are grepped out of logs and matched on by
    /// clients, so a code carrying a capital, a space or punctuation is a code somebody's filter will miss.
    /// </summary>
    [Fact]
    public void EveryCode_IsGreppableLowerSnakeCase()
    {
        var wrong = new List<string>();

        foreach (var reason in Enum.GetValues<StatsStoreUnavailableReason>())
        {
            var code = GatewayStatsStore.CodeFor(reason);
            if (!code.All(c => (c >= 'a' && c <= 'z') || c == '_'))
                wrong.Add($"{reason} -> '{code}'");
        }

        Assert.True(
            wrong.Count == 0,
            "These reason codes are not lower_snake_case, so a log filter or client match will miss them: " +
            string.Join(", ", wrong));
    }
}
