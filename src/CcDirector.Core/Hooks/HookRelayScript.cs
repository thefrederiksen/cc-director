namespace CcDirector.Core.Hooks;

/// <summary>
/// Contains the PowerShell hook relay script as an embedded string constant.
/// Written to disk at runtime so the app can be distributed as a single exe.
/// </summary>
public static class HookRelayScript
{
    /// <summary>
    /// Directory where the relay script is written at runtime.
    /// </summary>
    public static string ScriptDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CcDirector");

    /// <summary>
    /// Full path to the relay script on disk.
    /// </summary>
    public static string ScriptPath =>
        Path.Combine(ScriptDirectory, "hook-relay.ps1");

    /// <summary>
    /// Writes the relay script to disk, creating the directory if needed.
    /// Always overwrites to keep the script in sync with the running version.
    /// </summary>
    public static void EnsureWritten()
    {
        var dir = ScriptDirectory;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        File.WriteAllText(ScriptPath, Content);
    }

    /// <summary>
    /// The PowerShell relay script content.
    /// Reads Claude Code hook JSON from stdin and relays it to the Director named pipe.
    /// </summary>
    public const string Content = """
        # CC Director Hook Relay
        # Reads Claude Code hook JSON from stdin and relays it to the Director named pipe.
        # Called by Claude Code hooks with async: true, so ~200ms PowerShell startup is fine.

        try {
            $json = [Console]::In.ReadToEnd()
            if ([string]::IsNullOrWhiteSpace($json)) { exit 0 }

            $pipeName = "CC_ClaudeDirector"
            $pipe = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeName, [System.IO.Pipes.PipeDirection]::Out)

            try {
                $pipe.Connect(2000)  # 2 second timeout
                $writer = New-Object System.IO.StreamWriter($pipe)
                $writer.WriteLine($json.Trim())
                $writer.Flush()
                $writer.Close()
            }
            catch {
                # Silent failure if Director is not running
                exit 0
            }
            finally {
                $pipe.Dispose()
            }
        }
        catch {
            # Silent failure - don't interfere with Claude Code
            exit 0
        }
        """;
}
