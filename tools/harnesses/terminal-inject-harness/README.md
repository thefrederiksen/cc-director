# Terminal Inject Harness

Live proof harness for issue #1117.

This tool launches real Claude Code and Codex sessions in disposable repositories, injects Phase 2
prompt cases through direct, REST, and fleet routes, and writes:

- `summary.json`
- `summary.html`
- per-run raw terminal and screen artifacts

Example:

```powershell
dotnet run --project tools/harnesses/terminal-inject-harness -- --out docs/cencon/proof/issue-1117 --runs 1
```

On Windows, when running from inside another terminal agent, prefer the built apphost from Task
Scheduler so Claude Code does not inherit a nested pseudo-console. The harness is a `WinExe` and
writes file-based reports.

Useful filters:

```powershell
dotnet run --project tools/harnesses/terminal-inject-harness -- --agent ClaudeCode --case sentence --route direct
dotnet run --project tools/harnesses/terminal-inject-harness -- --agent Codex --route rest --timeout 180
```

The harness uses a throwaway repository under the output directory unless `--repo` is supplied.
Do not point `--repo` at the devthrottle working tree.
