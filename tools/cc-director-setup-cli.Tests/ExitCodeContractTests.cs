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

/// <summary>
/// The parser must refuse what it used to ignore. A silently-dropped option is worse than an error on
/// an unattended path: `install --role` with no value quietly installed the DEFAULT role, and
/// `install --release-dir` quietly went to GitHub instead of the directory the caller named - an
/// install doing something other than what it was told, and reporting success. An agent cannot see
/// that; an exit code it can.
/// </summary>
public sealed class StrictArgumentParsingTests
{
    [Fact]
    public void AKnownOptionWithNoValue_IsAUsageError()
    {
        Assert.Throws<UsageException>(() => CliArgs.Parse(["install", "--role"]));
        Assert.Throws<UsageException>(() => CliArgs.Parse(["install", "--release-dir"]));
        // Followed by another option is the same mistake: the value is missing.
        Assert.Throws<UsageException>(() => CliArgs.Parse(["install", "--role", "--json"]));
    }

    [Fact]
    public void AnUnknownOption_IsAUsageError()
    {
        Assert.Throws<UsageException>(() => CliArgs.Parse(["status", "--bogus"]));
        Assert.Throws<UsageException>(() => CliArgs.Parse(["install", "--rolle", "workstation"]));
    }

    [Fact]
    public void RealCommandLinesStillParse()
    {
        var a = CliArgs.Parse(["install", "--role", "workstation", "--json", "--log-file", "x.log"]);
        Assert.Equal("install", a.Command);
        Assert.Equal("workstation", a.Option("role"));
        Assert.Equal("x.log", a.Option("log-file"));
        Assert.True(a.HasFlag("json"));

        var b = CliArgs.Parse(["uninstall", "--dry-run"]);
        Assert.True(b.HasFlag("dry-run"));

        // Flags at the end, with nothing after them, are still flags.
        Assert.True(CliArgs.Parse(["status", "--json"]).HasFlag("json"));
    }
}
