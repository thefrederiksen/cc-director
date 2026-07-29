using CcDirector.Setup.Cli;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

/// <summary>
/// The exit codes are the unattended install path's contract. Its callers are scripts and coding
/// agents, which branch on the number and cannot read prose - so changing one silently changes the
/// behaviour of everything that drives it, including the canonical agent prompt in the installation
/// documentation.
///
/// These are deliberately literal. A test that asserted ExitCodes.Ok == ExitCodes.Ok would pass
/// through any renumbering and prove nothing.
/// </summary>
public sealed class ExitCodeContractTests
{
    [Fact]
    public void TheNumbersAreFixed()
    {
        Assert.Equal(0, ExitCodes.Ok);
        Assert.Equal(1, ExitCodes.Error);
        Assert.Equal(2, ExitCodes.Usage);
        Assert.Equal(3, ExitCodes.PrerequisiteMissing);
    }

    // Distinctness matters as much as the values: an agent that cannot tell "I asked wrongly" from
    // "it went wrong" will retry a malformed command for ever.
    [Fact]
    public void EveryOutcomeIsDistinguishable()
    {
        int[] codes = [ExitCodes.Ok, ExitCodes.Error, ExitCodes.Usage, ExitCodes.PrerequisiteMissing];
        Assert.Equal(codes.Length, codes.Distinct().Count());
    }
}
