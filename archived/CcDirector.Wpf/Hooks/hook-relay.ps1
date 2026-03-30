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
