# Claude Code Command Line Reference

Comprehensive reference for the Claude Code CLI, based on systematic testing of 65 scenarios
across 14 categories. Every example in this document has been executed and verified against
Claude Code v2.1.47 on Windows using the CcDirector.CliExplorer test harness.

---

## Quick Reference

```
claude [options] [prompt]
```

Claude Code starts an interactive session by default. Use `-p` / `--print` for non-interactive
(headless) output, which is how CC Director drives it programmatically.

---

## 1. Version and Help (Zero Cost)

These flags return immediately without making any API calls.

```bash
# Get version string
claude --version       # => "2.1.47 (Claude Code)"
claude -v              # Same thing, short form

# Print full help with all flags
claude --help
```

**Key discovery:** `--help` lists 60+ flags. These three are the only truly zero-cost operations
along with `--init-only`.

---

## 2. Print Mode (Non-Interactive)

Print mode (`-p`) is the foundation of headless usage. It reads a prompt from stdin or arguments,
sends it to Claude, and prints the response to stdout.

```bash
# Basic print mode: pipe prompt via stdin
echo "Say hello" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku
# => "hello"

# Long form
echo "Say hello" | claude --print --dangerously-skip-permissions --max-turns 1 --model haiku

# Empty stdin produces an error (exit code 1)
echo "" | claude -p --model haiku
# stderr: "Error: Input must be provided either through stdin or as a prompt argument when using --print"

# Ephemeral session: don't write session to disk
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --no-session-persistence
```

**Key flags for headless usage:**
- `-p` / `--print` -- Non-interactive mode (required)
- `--dangerously-skip-permissions` -- Bypass permission prompts (required for automation)
- `--max-turns 1` -- Prevent runaway loops (critical for cost control)
- `--model haiku` -- Cheapest model for simple tasks

### C# Example: Running Claude in Print Mode

```csharp
var psi = new ProcessStartInfo
{
    FileName = claudePath,
    Arguments = "-p --dangerously-skip-permissions --max-turns 1 --model haiku",
    RedirectStandardInput = true,
    RedirectStandardOutput = true,
    RedirectStandardError = true,
    UseShellExecute = false,
    CreateNoWindow = true,
    StandardOutputEncoding = Encoding.UTF8,
};

// CRITICAL: Remove this env var or Claude refuses to start inside another Claude session
psi.Environment.Remove("CLAUDECODE");

var process = new Process { StartInfo = psi };
process.Start();

await process.StandardInput.WriteAsync("Say just the word pong");
process.StandardInput.Close();

var stdout = await process.StandardOutput.ReadToEndAsync();
var stderr = await process.StandardError.ReadToEndAsync();
await process.WaitForExitAsync();
// stdout => "pong"
```

---

## 3. Output Formats

Three output formats control how Claude returns its response.

### Text (Default)

Plain text response. What you see in the terminal.

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format text
# => "pong"
```

### JSON

Single JSON blob returned after completion. Contains the response, session ID, cost, usage stats.

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json
```

Returns:
```json
{
  "type": "result",
  "subtype": "success",
  "is_error": false,
  "result": "pong",
  "session_id": "11a83681-8718-4d19-98df-e5ccac9b2c67",
  "total_cost_usd": 0.0024,
  "num_turns": 1,
  "duration_ms": 1887,
  "usage": {
    "input_tokens": 10,
    "output_tokens": 46,
    "cache_read_input_tokens": 21735
  }
}
```

**This is the best format for getting the session ID after a one-shot command.**

### Stream JSON

Newline-delimited JSON messages streamed in real-time. **Requires `--verbose` in print mode.**

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format stream-json --verbose
```

Emits messages like:
```json
{"type":"system","subtype":"init","session_id":"3cfa8fd7-...","tools":["Task","Bash","Read","Edit",...]}
{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"pong"}]}}
{"type":"result","subtype":"success","result":"pong","session_id":"3cfa8fd7-..."}
```

**Critical discovery:** The `session_id` appears in the very first messages (hook_started and
init), BEFORE any API call. This means you can capture the session ID instantly without waiting
for the response to complete.

#### With Partial Messages

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --output-format stream-json --verbose --include-partial-messages
```

Includes token-by-token partial messages as they stream in. Useful for live UI updates.

### Input Format

```bash
# Explicit text input (default)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --input-format text

# Stream-JSON input: requires stream-json output + verbose
echo '{"type":"user_message","content":"Say pong"}' | \
  claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --input-format stream-json --output-format stream-json --verbose
```

**Rules discovered through testing:**
- `--output-format stream-json` REQUIRES `--verbose` in print mode
- `--input-format stream-json` REQUIRES `--output-format stream-json`
- Violating these rules produces immediate errors (exit code 1)

---

## 4. System Prompts

Override or extend the default system prompt.

### Replace System Prompt

```bash
# Inline text
echo "Say hello" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --system-prompt "You are a pirate. Always respond in pirate speak."
# => "Ahoy, me hearty! Welcome aboard, matey!"

# From file
echo "Say hello" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --system-prompt-file path/to/system-prompt.txt
```

### Append to Default System Prompt

The default system prompt is preserved; your text is added to the end.

```bash
# Inline append
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --append-system-prompt "Always end your response with DONE."
# => "pong\nDONE"

# From file
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --append-system-prompt-file path/to/append-prompt.txt
```

### Combined

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --system-prompt "You are a helpful bot." --append-system-prompt "Always be concise."
```

---

## 5. Model Selection

```bash
# Available models (tested and verified)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku   # Cheapest, fastest
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model sonnet  # Balanced
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model opus    # Most capable

# Invalid model: exit code 1 with helpful error
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model invalid-model
# stdout: "There's an issue with the selected model (invalid-model). It may not exist or
#          you may not have access to it. Run --model to pick a different model."

# Fallback model: used if primary model fails
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 \
  --model sonnet --fallback-model haiku
```

**Cost tip:** Always use `--model haiku` for automation tasks where quality doesn't matter.

---

## 6. Execution Control

### Turn Limits

```bash
# Single turn (recommended for automation)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku

# Two turns: allows Claude to use a tool and then respond
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 2 --model haiku

# Zero turns: silently treated as 1 turn (does NOT error)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 0 --model haiku
# => Still responds normally
```

### Budget Limits

```bash
# Small budget constraint
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --max-budget-usd 0.01

# Zero budget: immediate failure (exit code 1)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --max-budget-usd 0
```

**Discovery:** `--max-turns 0` does NOT error or return immediately. It silently executes 1 turn.

---

## 7. Tools and Permissions

### Tool Control

```bash
# Disable all tools (Claude can only chat, no file access)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --tools ""

# Default toolset
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --tools default

# Specific tools only
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --tools "Read,Bash"

# Allow only specific tools (additive filter)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --allowedTools "Read"

# Allow Bash but only for git commands (pattern matching)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --allowedTools "Bash(git *)"

# Deny specific tools
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --disallowedTools "Bash"

# Both allow and deny together
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --allowedTools "Read,Bash" --disallowedTools "Write"
```

### Permission Modes

```bash
# Plan mode: Claude can only read, not write
echo "Say pong" | claude -p --max-turns 1 --model haiku --permission-mode plan

# Accept edits mode
echo "Say pong" | claude -p --max-turns 1 --model haiku --permission-mode acceptEdits

# Bypass all permissions (same as --dangerously-skip-permissions)
echo "Say pong" | claude -p --max-turns 1 --model haiku --permission-mode bypassPermissions
```

### Disable Skills

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --disable-slash-commands
```

---

## 8. JSON Schema (Structured Output)

Force Claude to return structured JSON matching a specific schema.

```bash
# Inline JSON schema (Windows: use escaped double quotes, NOT single quotes)
echo "What is 2+2?" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --output-format json \
  --json-schema "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"]}"

# Multi-field schema
echo "What is 2+2? Rate your confidence." | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --output-format json \
  --json-schema "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"},\"confidence\":{\"type\":\"number\"}},\"required\":[\"answer\",\"confidence\"]}"
```

### C# Example: Structured Output

```csharp
// CRITICAL: On Windows, use escaped double quotes for JSON schema.
// Single quotes do NOT work on Windows (they are literal characters, not argument delimiters).
// This caused 30s+ hangs in testing until we fixed the quoting.
var schema = "{\\\"type\\\":\\\"object\\\",\\\"properties\\\":{\\\"answer\\\":{\\\"type\\\":\\\"string\\\"}},\\\"required\\\":[\\\"answer\\\"]}";
var args = $"-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json --json-schema \"{schema}\"";
```

**Windows quoting gotcha:** Single quotes (`'{"type":"object"}'`) are literal characters on
Windows, not argument delimiters. The JSON gets mangled and Claude hangs indefinitely.
Always use escaped double quotes on Windows.

---

## 9. Session Management

### Create with Explicit Session ID

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --session-id 8bf04ad2-de0a-4581-8398-d3e55f644e35
```

### Continue Most Recent Session

```bash
echo "What was my last question?" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --continue
# Short form: -c
```

### Resume a Specific Session

```bash
echo "Continue from where we left off" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --resume 550e8400-e29b-41d4-a716-446655440000
```

**Error behavior:** Invalid session IDs produce a clear error:
```
Error: --resume requires a valid session ID when used with --print.
Session IDs must be in UUID format (e.g., 550e8400-e29b-41d4-a716-446655440000).
```

### Fork a Session

Create a branch of an existing session:

```bash
echo "Let's try a different approach" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --continue --fork-session
```

### Ephemeral Sessions

```bash
echo "One-shot question" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --no-session-persistence
```

---

## 10. Directories and Settings

### Additional Directory Context

```bash
# Give Claude access to another directory
echo "Summarize the core project" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --add-dir ../CcDirector.Core
```

### Settings Sources

```bash
# Restrict to user-level settings only (ignore project-level)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --setting-sources user

# Pass a custom settings file
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --settings ./my-settings.json
```

---

## 11. Debug and Verbose

```bash
# General debug output (sent to stderr)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --debug

# Debug only API calls
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --debug api

# Verbose mode (required for stream-json in print mode)
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku --verbose
```

---

## 12. Init and Maintenance (Zero Cost)

```bash
# Initialize project settings without starting a session
claude --init-only

# Run maintenance tasks (requires -p mode with stdin)
echo "done" | claude -p --maintenance
```

---

## 13. Agents and MCP

### MCP Server Configuration

```bash
# Load MCP servers from a config file
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --mcp-config path/to/mcp-config.json

# Strict validation: fail if MCP servers can't connect
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --mcp-config path/to/mcp-config.json --strict-mcp-config
```

MCP config file format:
```json
{
  "mcpServers": {}
}
```

### Custom Agents

```bash
echo "Review this code" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --agents '{"reviewer": {"description": "Reviews code", "prompt": "You are a code reviewer"}}'
```

---

## 14. Practical Combinations

Real-world usage patterns combining multiple flags.

### Cheapest Structured Output

```bash
echo "What is 2+2?" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --output-format json \
  --json-schema "{\"type\":\"object\",\"properties\":{\"answer\":{\"type\":\"string\"}},\"required\":[\"answer\"]}"
```

### Stream JSON with Verbose (for Session ID Capture)

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --output-format stream-json --verbose
```

### No Tools + Custom System Prompt

```bash
echo "What is 2+2?" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --tools "" --system-prompt "You are a simple calculator."
```

### Budget-Constrained Multi-Turn

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 2 --model haiku --max-budget-usd 0.01
```

### Ephemeral Single-Turn (No Disk Writes)

```bash
echo "Say pong" | claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --no-session-persistence --output-format text
```

### Full JSON Pipeline

```bash
echo '{"type":"user_message","content":"Say pong"}' | \
  claude -p --dangerously-skip-permissions --max-turns 1 --model haiku \
  --input-format stream-json --output-format stream-json --verbose
```

---

## Session ID Discovery

One of the most valuable discoveries from our testing: you can capture the session ID
from Claude's stdout instantly, before any API call completes.

### Method 1: JSON Output (After Completion)

Use `--output-format json` and parse the `session_id` field from the response:

```csharp
var result = await runner.RunAsync(
    "-p --dangerously-skip-permissions --max-turns 1 --model haiku --output-format json",
    stdinText: "Say pong");

using var doc = JsonDocument.Parse(result.Stdout);
var sessionId = doc.RootElement.GetProperty("session_id").GetString();
```

### Method 2: Stream JSON Init Message (Instant)

Use `--output-format stream-json --verbose` and read the session ID from the first
few lines before any API call:

```csharp
// The very first stdout messages contain the session_id:
// {"type":"system","subtype":"hook_started","session_id":"3cfa8fd7-..."}
// {"type":"system","subtype":"init","session_id":"3cfa8fd7-...","tools":[...]}

var result = await ClaudeProcess.StartAndGetSessionIdAsync(
    claudePath,
    "--dangerously-skip-permissions --max-turns 1 --model haiku",
    workingDirectory);

// result.SessionId is available INSTANTLY (before API response)
// result.Process is still running -- caller owns its lifecycle
```

See `CcDirector.Core/Claude/ClaudeProcess.cs` for the full implementation.

---

## Windows-Specific Gotchas

These issues were discovered through testing and cost significant debugging time.

### 1. CLAUDECODE Environment Variable

When launching Claude from within a Claude Code session (e.g., from CC Director), the
`CLAUDECODE` environment variable is set. Claude checks this and refuses to start:

```
Error: Claude Code cannot be launched inside another Claude Code session.
```

**Fix:** Remove the variable from the child process environment:

```csharp
psi.Environment.Remove("CLAUDECODE");
```

### 2. Single Quotes Don't Work on Windows

Windows command-line parsing uses double quotes, not single quotes. Single quotes are
treated as literal characters. This means:

```bash
# BROKEN on Windows: single quotes are literal, JSON gets mangled, process hangs
--json-schema '{"type":"object"}'

# WORKS on Windows: escaped double quotes
--json-schema "{\"type\":\"object\"}"
```

In C# ProcessStartInfo.Arguments:
```csharp
// BROKEN: Single quotes become part of the argument
var args = "--json-schema '{\"type\":\"object\"}'";

// WORKS: Properly escaped for Windows argument parsing
var args = "--json-schema \"{\\\"type\\\":\\\"object\\\"}\"";
```

### 3. Pipe Deadlock with Stream Reading

When reading stdout line-by-line (e.g., to capture session ID) and then calling
`WaitForExitAsync`, the process can deadlock if stdout isn't fully drained:

```csharp
// BROKEN: Deadlock if Claude writes more to stdout than the pipe buffer holds
var sessionId = await ReadSessionIdFromStream(process.StandardOutput);
await process.WaitForExitAsync(); // HANGS -- pipe buffer full, Claude blocked on write

// FIXED: Drain remaining stdout in background before waiting
var sessionId = await ReadSessionIdFromStream(process.StandardOutput);
var drainStdout = process.StandardOutput.ReadToEndAsync();  // Background drain
var drainStderr = process.StandardError.ReadToEndAsync();
await process.WaitForExitAsync();
await Task.WhenAll(drainStdout, drainStderr);
```

---

## Complete Flag Reference

Extracted from `claude --help` (v2.1.47):

| Flag | Description |
|------|-------------|
| `-p`, `--print` | Non-interactive mode, output to stdout |
| `-c`, `--continue` | Continue most recent conversation |
| `-r`, `--resume <id>` | Resume a specific session by UUID |
| `--model <name>` | Select model: haiku, sonnet, opus |
| `--fallback-model <name>` | Fallback if primary model fails |
| `--max-turns <n>` | Maximum agentic turns |
| `--max-budget-usd <n>` | Maximum cost in USD |
| `--output-format <fmt>` | text, json, stream-json |
| `--input-format <fmt>` | text, stream-json |
| `--include-partial-messages` | Token-by-token streaming (with stream-json) |
| `--json-schema <json>` | Force structured JSON output |
| `--system-prompt <text>` | Replace system prompt |
| `--system-prompt-file <path>` | Replace system prompt from file |
| `--append-system-prompt <text>` | Append to default system prompt |
| `--append-system-prompt-file <path>` | Append from file |
| `--tools <list>` | Available tools: "", "default", or comma-separated |
| `--allowedTools <list>` | Allow specific tools (supports patterns) |
| `--disallowedTools <list>` | Deny specific tools |
| `--permission-mode <mode>` | plan, acceptEdits, rejectEdits, bypassPermissions |
| `--dangerously-skip-permissions` | Bypass all permission checks |
| `--session-id <uuid>` | Create session with explicit UUID |
| `--fork-session` | Fork from an existing session |
| `--no-session-persistence` | Don't write session to disk |
| `--add-dir <dirs>` | Additional directory access |
| `--setting-sources <src>` | Restrict settings: user, project |
| `--settings <path>` | Custom settings file |
| `--debug [filter]` | Debug output (optional category filter) |
| `--verbose` | Verbose logging (required for stream-json in -p) |
| `--init-only` | Initialize without starting session |
| `--maintenance` | Run maintenance tasks |
| `--mcp-config <path>` | MCP server configuration file |
| `--strict-mcp-config` | Fail if MCP servers can't connect |
| `--agents <json>` | Define custom agents inline |
| `--disable-slash-commands` | Disable all skills |
| `--version`, `-v` | Print version |
| `--help` | Print help |

---

## Test Results

All 65 scenarios pass (verified 2026-02-19, Claude Code v2.1.47):

| Category | Scenarios | Status |
|----------|-----------|--------|
| Version and Help | 3 | All Pass |
| Print Mode | 4 | All Pass |
| Output Formats | 6 | All Pass |
| System Prompts | 5 | All Pass |
| Model Selection | 5 | All Pass |
| Execution Control | 5 | All Pass |
| Tools and Permissions | 10 | All Pass |
| JSON Schema | 2 | All Pass |
| Session Management | 4 | All Pass |
| Directories and Settings | 3 | All Pass |
| Debug and Verbose | 3 | All Pass |
| Init and Maintenance | 2 | All Pass |
| Agents and MCP | 3 | All Pass |
| Combinations | 10 | All Pass |
| **Total** | **65** | **65/65 Pass** |

Test harness: `src/CcDirector.CliExplorer/`
Full test report: `cli-explorer-report.md`
