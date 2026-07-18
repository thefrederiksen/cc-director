using CcDirector.Setup.Cli;
using Xunit;

namespace CcDirector.Setup.Cli.Tests;

/// <summary>
/// Guards the managed Gateway install/update pre-flight (D2a). The ONLY hard requirement is the
/// platform: there is NO OPENAI_API_KEY demand, because inference routes through the account-minted
/// dt_live_ key the managed Gateway runtime mints itself. Demanding an OpenAI key here used to block a
/// Gateway refresh on machines that never had one.
/// </summary>
public class GatewayInstallPreflightTests
{
    // The headline D2a guard: on Windows the pre-flight passes with NO OPENAI_API_KEY anywhere -
    // Check takes no key and reads no environment, so it CANNOT demand one.
    // Revert-proof: re-add an OpenAI-key demand (an openAiKey parameter that fails when blank, the
    // shape this slice deleted) and this no-key call goes red - Check returns the key-demand error
    // instead of null.
    [Fact]
    public void Check_Windows_NoOpenAiKeyDemanded_ReturnsNull()
    {
        Assert.Null(GatewayInstallPreflight.Check(isWindows: true));
    }

    [Fact]
    public void Check_NonWindows_FailsWindowsOnly()
    {
        var result = GatewayInstallPreflight.Check(isWindows: false);
        Assert.NotNull(result);
        Assert.Contains("Windows-only", result);
    }

    // The pre-flight decision is a pure function of the platform - nothing about a key, so nothing an
    // ambient environment variable can change. This locks that no key concept leaked back in.
    [Fact]
    public void Check_MentionsNoOpenAiKey()
    {
        Assert.DoesNotContain("OPENAI", GatewayInstallPreflight.Check(isWindows: false), System.StringComparison.OrdinalIgnoreCase);
    }
}
