namespace CcDirector.Setup.Cli;

/// <summary>
/// Tiny argument parser: a leading positional command, then "--key value" pairs
/// and "--flag" switches. No external dependency; deterministic and easy to test.
/// </summary>
public sealed class CliArgs
{
    public string Command { get; }
    public IReadOnlyList<string> Positionals { get; }
    private readonly Dictionary<string, string> _options;
    private readonly HashSet<string> _flags;

    private static readonly HashSet<string> KnownFlags =
        new(StringComparer.OrdinalIgnoreCase) { "json", "dry-run", "help", "hosted" };

    private CliArgs(string command, List<string> positionals, Dictionary<string, string> options, HashSet<string> flags)
    {
        Command = command;
        Positionals = positionals;
        _options = options;
        _flags = flags;
    }

    /// <summary>
    /// Options that TAKE A VALUE. Kept beside <see cref="KnownFlags"/> so the parser can tell "you
    /// forgot the value" from "that is a switch" from "that is not a thing" - three different mistakes
    /// that all used to look like a successful command.
    /// </summary>
    private static readonly HashSet<string> KnownOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "component", "gateway", "log-file", "manifest", "release-dir", "role", "root", "tools",
    };

    public static CliArgs Parse(string[] argv)
    {
        var command = argv.Length > 0 && !argv[0].StartsWith("--", StringComparison.Ordinal) ? argv[0] : "help";
        var positionals = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var flags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int start = command == "help" && (argv.Length == 0 || argv[0] != "help") ? 0 : 1;
        for (int i = start; i < argv.Length; i++)
        {
            var a = argv[i];
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                var key = a[2..];
                if (KnownFlags.Contains(key))
                {
                    flags.Add(key);
                }
                else if (KnownOptions.Contains(key))
                {
                    // A known option with no value is a MISTAKE, not a flag. Treating it as one meant
                    // "install --role" quietly installed the default role and "install --release-dir"
                    // quietly went to GitHub instead of the directory the caller asked for - an
                    // unattended install doing something other than what it was told, and reporting
                    // success. Scripts and agents cannot see that; an exit code they can.
                    if (i + 1 >= argv.Length || argv[i + 1].StartsWith("--", StringComparison.Ordinal))
                        throw new UsageException($"--{key} needs a value.");
                    options[key] = argv[++i];
                }
                else
                {
                    // An unknown option is a mistake too. Accepting it silently means a typo in an
                    // agent's command line looks like a successful install of something else.
                    throw new UsageException($"Unknown option --{key}.");
                }
            }
            else
            {
                positionals.Add(a);
            }
        }

        return new CliArgs(command, positionals, options, flags);
    }

    public bool HasFlag(string name) => _flags.Contains(name);
    public string? Option(string name) => _options.TryGetValue(name, out var v) ? v : null;
    public string Option(string name, string fallback) => _options.TryGetValue(name, out var v) ? v : fallback;
}
