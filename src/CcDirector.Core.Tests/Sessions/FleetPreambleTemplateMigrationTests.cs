using CcDirector.Core.Account;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// MIGRATION PROOF. The preamble used to be assembled with C# string interpolation; it is now a
/// template rendered by substitution. This asserts the change was a pure refactor: for every shape of
/// session, the new renderer produces text BYTE-IDENTICAL to the old builder.
///
/// LegacyFleetPreamble is the old implementation, copied mechanically from the commit before this one
/// with only its namespace and class name changed - not retyped, because a transcription slip would
/// land in both the template and the expectation and this test would pass while both were wrong.
///
/// WHY THIS IS STILL HERE, AND WHEN IT GOES. A frozen copy of the old builder must NOT become the
/// permanent oracle for this text: the moment we intentionally reword the default - which we intend
/// to do, and which the user may now do for themselves - an old builder is simply the wrong answer.
/// But it is the only byte-for-byte guard that currently exists; the other tests assert substrings
/// and policy lines, which would not notice a change to spacing, indentation, or the gap left when
/// nobody is signed in. So it stays until the SAME change that adds a lasting golden test against an
/// approved snapshot of FleetPreambleTemplate.Default - a snapshot that can be updated deliberately,
/// where the diff shows a reviewer exactly what changed in the text reaching every agent. Deleting it
/// before then would trade a real guard for no guard.
/// </summary>
public class FleetPreambleTemplateMigrationTests
{
    private const string Id = "a3dfb85e-49dd-442a-9e36-40fc44838783";

    public static TheoryData<string, string?, string, string, SignedInUser?> Sessions() => new()
    {
        // Signed in, with a nickname - the everyday case.
        { Id, "devthrottle", "MACHINE_A", @"C:\repos\devthrottle", new SignedInUser("soren@example.com", "Starlord") },
        // Signed in, no nickname - the identity line falls back to the email as the display name.
        { Id, "devthrottle", "MACHINE_A", @"C:\repos\devthrottle", new SignedInUser("soren@example.com", null) },
        // Nobody signed in - the whole identity line must vanish, leaving no blank line behind.
        { Id, "devthrottle", "MACHINE_A", @"C:\repos\devthrottle", null },
        // An unnamed session renders the "(unnamed)" placeholder.
        { Id, null, "MACHINE_A", @"C:\repos\devthrottle", null },
        { Id, "", "MACHINE_A", @"C:\repos\devthrottle", null },
        { Id, "   ", "MACHINE_A", @"C:\repos\devthrottle", null },
        // A signed-in user with a blank email is NOT signed in as far as the identity line goes.
        { Id, "devthrottle", "MACHINE_A", @"C:\repos\devthrottle", new SignedInUser("", "Starlord") },
        { Id, "devthrottle", "MACHINE_A", @"C:\repos\devthrottle", new SignedInUser("   ", "Starlord") },
        // A session id shorter than the eight characters the short id wants.
        { "abc", "devthrottle", "MACHINE_A", @"C:\repos\devthrottle", null },
        { "", "devthrottle", "MACHINE_A", @"C:\repos\devthrottle", null },
        // Values that are themselves bracket-shaped must not be treated as placeholders.
        { Id, "[SESSION_ID]", "MACHINE_A", @"C:\repos\devthrottle", null },
        { Id, "devthrottle", "[MACHINE]", @"C:\repos\[REPO_PATH]", null },
        // A repo path with spaces and a trailing backslash.
        { Id, "my repo", "MACHINE_A", @"C:\Program Files\repos\", null },
    };

    [Theory]
    [MemberData(nameof(Sessions))]
    public void Render_MatchesTheOldBuilder_Exactly(
        string sessionId, string? name, string machine, string repoPath, SignedInUser? user)
    {
        var legacy = LegacyFleetPreamble.Build(sessionId, name, machine, repoPath, user);
        var rendered = FleetPreamble.Build(sessionId, name, machine, repoPath, user);

        Assert.Equal(legacy, rendered);
    }
}
