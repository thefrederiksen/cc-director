using System.Runtime.InteropServices;
using CcDirector.Core.Storage;

namespace CcDirector.Core.Hooks;

/// <summary>
/// Contains the hook relay script as an embedded string constant.
/// Written to disk at runtime so the app can be distributed as a single exe.
/// Windows uses PowerShell, Unix uses Python.
/// </summary>
public static class HookRelayScript
{
    /// <summary>
    /// Directory where the relay script is written at runtime.
    /// </summary>
    public static string ScriptDirectory => CcStorage.ToolConfig("director");

    /// <summary>
    /// Full path to the relay script on disk.
    /// Windows: hook-relay.ps1 (PowerShell)
    /// Unix: hook-relay.py (Python)
    /// </summary>
    public static string ScriptPath =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? Path.Combine(ScriptDirectory, "hook-relay.ps1")
            : Path.Combine(ScriptDirectory, "hook-relay.py");

    /// <summary>
    /// Writes the relay script to disk, creating the directory if needed.
    /// Always overwrites to keep the script in sync with the running version.
    /// </summary>
    public static void EnsureWritten()
    {
        var dir = ScriptDirectory;
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var content = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? WindowsContent
            : UnixContent;

        File.WriteAllText(ScriptPath, content);

        // On Unix, make the script executable
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            try
            {
                // chmod +x using Process
                var chmod = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "chmod",
                        Arguments = $"+x \"{ScriptPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                chmod.Start();
                chmod.WaitForExit(5000);
            }
            catch
            {
                // Ignore chmod failures - script may still work
            }
        }
    }

    /// <summary>
    /// The PowerShell relay script content (Windows).
    /// Reads Claude Code hook JSON from stdin and relays it to the Director named pipe.
    /// </summary>
    public const string WindowsContent = """
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

    /// <summary>
    /// The Python relay script content (macOS/Linux).
    /// Reads Claude Code hook JSON from stdin and relays it to the Director Unix socket.
    /// </summary>
    public const string UnixContent =
        """
        #!/usr/bin/env python3
        # CC Director Hook Relay (Unix)
        # Reads Claude Code hook JSON from stdin and relays it to the Director Unix socket.
        # Called by Claude Code hooks with async: true.

        import socket
        import sys
        import os

        SOCKET_PATH = os.path.expanduser("~/.cc_director/director.sock")

        def main():
            # Check if socket exists
            if not os.path.exists(SOCKET_PATH):
                sys.exit(0)  # Silent exit if Director not running

            # Read JSON from stdin
            try:
                data = sys.stdin.read().strip()
            except Exception:
                sys.exit(0)

            if not data:
                sys.exit(0)

            # Send to Unix socket
            try:
                with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as s:
                    s.settimeout(2.0)  # 2 second timeout
                    s.connect(SOCKET_PATH)
                    s.sendall((data + "\n").encode("utf-8"))
            except Exception:
                # Silent failure - don't interfere with Claude Code
                sys.exit(0)

        if __name__ == "__main__":
            main()
        """;

    /// <summary>
    /// Legacy property for backwards compatibility.
    /// Returns Windows content (original behavior).
    /// </summary>
    public static string Content => WindowsContent;
}
