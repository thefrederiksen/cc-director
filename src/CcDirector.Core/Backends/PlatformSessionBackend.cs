using System.Runtime.InteropServices;

namespace CcDirector.Core.Backends;

/// <summary>
/// Selects the pseudo-console session backend for the current operating system: the Windows
/// ConPty on Windows, the Unix pseudo-terminal on macOS and Linux. This is the ONE place that
/// choice is made, so the shared library never hardcodes a Windows-only terminal on a host that
/// has no ConPty (the warm brain and the wingman ask both used to default straight to
/// <see cref="ConPtyBackend"/>, which throws on a non-Windows host).
/// </summary>
public static class PlatformSessionBackend
{
    /// <summary>The default backend buffer size, matching each backend's own default.</summary>
    public const int DefaultBufferSizeBytes = 2 * 1024 * 1024;

    /// <summary>
    /// Create the pseudo-console backend for the current platform: <see cref="ConPtyBackend"/>
    /// on Windows, <see cref="UnixPtyBackend"/> on macOS and Linux.
    /// </summary>
    public static ISessionBackend CreateDefault(int bufferSizeBytes = DefaultBufferSizeBytes) =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ConPtyBackend(bufferSizeBytes)
            : new UnixPtyBackend(bufferSizeBytes);
}
