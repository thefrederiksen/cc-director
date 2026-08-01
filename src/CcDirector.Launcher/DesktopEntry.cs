using System.Text;

namespace CcDirector.Launcher;

/// <summary>
/// A Linux desktop entry - the ".desktop" file that IS the catalogue entry on Linux - and the one
/// thing the launcher needs from it: which program to start.
///
/// Tenant-boundary hardening, Phase 5b, inspection finding M03-I2-02. Phase 1 narrowed the launch
/// verb so that only a catalogued application can be started. On Linux the catalogue is the desktop
/// entry directories, and every entry in it is a ".desktop" FILE - which is data describing a
/// program, not the program. The launcher then handed that file to the macOS arm, which starts a
/// path as a plain executable. So after the allowlist landed, the only paths Linux would accept were
/// paths the launcher could not start, and a caller could no longer name the real executable either.
/// That is a product capability deleted on a supported platform, which the mission brief forbids -
/// the allowlist was right, the handoff behind it was not.
///
/// This class is the handoff: read the entry, take its own Exec line, and start THAT. The security
/// property is unchanged and is the reason the parsing lives here rather than in the caller - what
/// runs comes from the catalogued file on the machine, never from anything the caller sent.
/// </summary>
internal sealed record DesktopEntry(string Type, string? Exec, string? WorkingDirectory, bool Terminal)
{
    /// <summary>
    /// The field codes the Desktop Entry Specification defines for an Exec line. They are
    /// placeholders a desktop environment fills in with the files or URLs a user dropped on the
    /// icon - there is no such thing here, so an argument carrying one is dropped entirely.
    /// </summary>
    private const string FieldCodes = "fFuUdDnNickvm";

    /// <summary>
    /// Read the "[Desktop Entry]" group of a desktop entry file. Only that group is read: an entry
    /// may also carry "[Desktop Action ...]" groups describing extra menu items, and those are a
    /// different program to start, not this one.
    /// </summary>
    internal static DesktopEntry Read(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Desktop entry not found: {path}", path);

        string? type = null, exec = null, workingDirectory = null, terminal = null;
        var inDesktopEntryGroup = false;

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                inDesktopEntryGroup = string.Equals(line, "[Desktop Entry]", StringComparison.Ordinal);
                continue;
            }

            if (!inDesktopEntryGroup)
                continue;

            var separator = line.IndexOf('=');
            if (separator <= 0)
                continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            switch (key)
            {
                case "Type": type = value; break;
                case "Exec": exec = value; break;
                case "Path": workingDirectory = value; break;
                case "Terminal": terminal = value; break;
            }
        }

        // Type is required of every entry. Exec is NOT required here, because a Link or Directory
        // entry legitimately has none - refusing those on a missing Exec would report the wrong
        // reason for something this launcher declines for a different and clearer one.
        if (string.IsNullOrWhiteSpace(type))
            throw new InvalidOperationException($"Desktop entry has no Type: {path}");

        return new DesktopEntry(
            type,
            string.IsNullOrWhiteSpace(exec) ? null : exec,
            string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory,
            string.Equals(terminal, "true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Split an Exec value into the argument vector it describes: the program first, then its own
    /// arguments. Quoting follows the specification - a double quote groups, and inside quotes a
    /// backslash escapes a quote, a backtick, a dollar sign or another backslash.
    ///
    /// An argument containing a FIELD CODE is dropped whole. Field codes stand for the files or URLs
    /// a desktop environment would substitute, and there are none here; dropping the argument rather
    /// than the two characters avoids handing the program a half-written option like "--file=" that
    /// it never asked for.
    ///
    /// A percent sign that is neither "%%" nor a defined field code is a MALFORMED entry - the
    /// specification requires a literal percent to be written "%%" - and it is refused rather than
    /// guessed at. Refusing to start something is recoverable; starting the wrong thing is not.
    /// </summary>
    internal static IReadOnlyList<string> ParseExec(string exec)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var argumentStarted = false;
        var argumentHadFieldCode = false;
        var inQuotes = false;

        void Flush()
        {
            if (!argumentHadFieldCode && (argumentStarted || current.Length > 0))
                arguments.Add(current.ToString());
            current.Clear();
            argumentStarted = false;
            argumentHadFieldCode = false;
        }

        for (var i = 0; i < exec.Length; i++)
        {
            var c = exec[i];

            if (inQuotes)
            {
                if (c == '\\' && i + 1 < exec.Length && "\"`$\\".Contains(exec[i + 1]))
                {
                    current.Append(exec[++i]);
                    continue;
                }
                if (c == '"') { inQuotes = false; continue; }
                current.Append(c);
                continue;
            }

            if (c == '"') { inQuotes = true; argumentStarted = true; continue; }

            if (char.IsWhiteSpace(c)) { Flush(); continue; }

            if (c == '%')
            {
                if (i + 1 >= exec.Length)
                    throw new InvalidOperationException(
                        $"Desktop entry Exec line ends with a bare percent sign, which the Desktop Entry " +
                        $"Specification does not allow: {exec}");
                var code = exec[++i];
                if (code == '%') { current.Append('%'); argumentStarted = true; continue; }
                if (FieldCodes.Contains(code)) { argumentHadFieldCode = true; continue; }
                throw new InvalidOperationException(
                    $"Desktop entry Exec line carries an unknown field code '%{code}'. A literal percent " +
                    $"must be written '%%', so this entry is malformed and will not be started: {exec}");
            }

            current.Append(c);
            argumentStarted = true;
        }

        if (inQuotes)
            throw new InvalidOperationException($"Desktop entry Exec line has an unterminated quote: {exec}");

        Flush();

        if (arguments.Count == 0)
            throw new InvalidOperationException($"Desktop entry Exec line names no program to start: {exec}");

        return arguments;
    }
}
