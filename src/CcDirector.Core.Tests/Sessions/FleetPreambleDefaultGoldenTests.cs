using System.Runtime.CompilerServices;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// THE GOLDEN GUARD for the shipped fleet preamble. FleetPreambleTemplate.Default must equal the
/// approved snapshot beside this file, byte-for-byte (modulo line endings). This is the lasting guard
/// the migration test (Render_MatchesTheOldBuilder_Exactly, retired with the first deliberate
/// rewording - the Workflows phase 5 [WORKFLOW_INDEX] block) promised as its replacement: a frozen
/// legacy BUILDER was the wrong oracle the moment the text was allowed to change on purpose, but a
/// SNAPSHOT that is updated deliberately keeps the byte-for-byte protection while making every change
/// to the text reaching every agent show up as a reviewable diff of the approved file.
///
/// To change the shipped preamble ON PURPOSE: edit FleetPreambleTemplate.Default, run this test, and
/// copy the .received.txt it writes over the .approved.txt - that copy step is the deliberate
/// approval, and the pull request diff of the approved file is what the reviewer reads.
/// </summary>
public sealed class FleetPreambleDefaultGoldenTests
{
    [Fact]
    public void TheShippedDefault_MatchesTheApprovedSnapshot()
    {
        var dir = Path.GetDirectoryName(SourcePath())!;
        var approvedPath = Path.Combine(dir, "fleet-preamble-default.approved.txt");
        var actual = FleetPreambleTemplate.Default.Replace("\r\n", "\n");

        if (!File.Exists(approvedPath))
        {
            File.WriteAllText(Path.Combine(dir, "fleet-preamble-default.received.txt"), actual);
            Assert.Fail($"No approved snapshot at {approvedPath}. The current template was written " +
                        "beside it as .received.txt; review it and rename it to .approved.txt to approve.");
        }

        var approved = File.ReadAllText(approvedPath).Replace("\r\n", "\n").TrimEnd('\n');

        if (!string.Equals(approved, actual.TrimEnd('\n'), StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(dir, "fleet-preamble-default.received.txt"), actual);
            Assert.Fail("FleetPreambleTemplate.Default no longer matches the approved snapshot. If the " +
                        "change is deliberate, copy fleet-preamble-default.received.txt over " +
                        "fleet-preamble-default.approved.txt so the diff is reviewed; if not, the " +
                        "received file shows what drifted.");
        }
    }

    private static string SourcePath([CallerFilePath] string path = "") => path;
}
