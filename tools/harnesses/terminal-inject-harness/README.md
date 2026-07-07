# Terminal Inject Harness

Live proof harness for terminal injection reliability issues #1117 and #1118.

This tool launches real Claude Code and Codex sessions in disposable repositories, injects
sentinel prompt cases through direct, REST, fleet, and voice-turn routes, and writes:

- `summary.json`
- `summary.html`
- per-run raw terminal and screen artifacts

Example:

```powershell
dotnet run --project tools/harnesses/terminal-inject-harness -- --out docs/cencon/proof/issue-1118 --runs 1
```

On Windows, when running from inside another terminal agent, prefer the built apphost from Task
Scheduler so Claude Code does not inherit a nested pseudo-console. The harness is a `WinExe` and
writes file-based reports.

Useful filters:

```powershell
dotnet run --project tools/harnesses/terminal-inject-harness -- --agent ClaudeCode --case sentence --route direct
dotnet run --project tools/harnesses/terminal-inject-harness -- --agent Codex --route rest --timeout 180
dotnet run --project tools/harnesses/terminal-inject-harness -- --case medium-line,large-line,multiline --submit-strategy all --allow-failures
```

Submit strategies:

- `current`: the product path for each route.
- `bracketed-paste`: harness-only raw PTY paste, gated on observed mode 2004. Routes that cannot
  express raw PTY input record `not_applicable`.

Use `--allow-failures` for comparison runs where failing cells are expected evidence rather than a
failed harness execution.

The harness uses a throwaway repository under the output directory unless `--repo` is supplied.
Do not point `--repo` at the devthrottle working tree.
