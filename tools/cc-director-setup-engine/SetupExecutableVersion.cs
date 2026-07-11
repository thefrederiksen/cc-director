using System.Reflection;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Reads the version stamped onto the running setup executable at build time. Every binary
/// - including the setup wizard and the setup CLI - is stamped from Directory.Build.props via
/// AssemblyInformationalVersion, and the setup exe is the process entry assembly, so a
/// v1.1.0-rc4 setup build self-identifies as "1.1.0-rc4". The SDK appends "+commit" SourceLink
/// metadata to that string; this strips it, leaving the plain version.
///
/// The setup engine uses this to install the release the setup exe was BUILT for: a pre-release
/// setup exe installs its matching pre-release rather than the latest stable (issue #1294).
/// </summary>
public static class SetupExecutableVersion
{
    /// <summary>
    /// The running setup executable's stamped version (for example "1.1.0-rc4" or "1.1.0").
    /// Returns "" when the entry assembly or its version stamp cannot be read; the caller
    /// treats an unknown version as stable (the latest-stable install path).
    /// </summary>
    public static string Read()
    {
        var info = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return Strip(info);
    }

    /// <summary>Drop the "+commit" SourceLink metadata, leaving "1.1.0-rc4" (or "1.1.0", or "").</summary>
    internal static string Strip(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return "";
        var plus = informationalVersion.IndexOf('+');
        return (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
    }
}
