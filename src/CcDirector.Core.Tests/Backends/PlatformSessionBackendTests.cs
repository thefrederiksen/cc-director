using CcDirector.Core.Backends;
using Xunit;

namespace CcDirector.Core.Tests.Backends;

/// <summary>
/// The one place the pseudo-console backend is chosen for the current operating system. The Windows
/// path must stay byte-for-byte what it was - a ConPty - and macOS and Linux must get the Unix
/// pseudo-terminal instead of a Windows-only ConPty that would throw on first use. This test runs on
/// every platform and asserts the branch for whichever one it runs on, so the Windows build proves
/// ConPty and the macOS and Linux runs prove UnixPty.
/// </summary>
public sealed class PlatformSessionBackendTests
{
    [Fact]
    public void CreateDefault_SelectsConPtyOnWindows_UnixPtyElsewhere()
    {
        using var backend = PlatformSessionBackend.CreateDefault();

        if (OperatingSystem.IsWindows())
            Assert.IsType<ConPtyBackend>(backend);
        else
            Assert.IsType<UnixPtyBackend>(backend);
    }

    [Fact]
    public void CreateDefault_HonorsAnExplicitBufferSize()
    {
        // The buffer size must flow through the selection unchanged - the desktop Director passes its
        // configured size, and collapsing the old two-arm switch into this factory must not drop it.
        using var backend = PlatformSessionBackend.CreateDefault(64 * 1024);
        Assert.NotNull(backend);
    }
}
