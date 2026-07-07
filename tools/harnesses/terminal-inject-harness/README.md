# Terminal Inject Harness

Live proof harness for terminal injection reliability issues #1117 through #1119.

This tool launches real installed built-in agent sessions in disposable repositories, injects
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
dotnet run --project tools/harnesses/terminal-inject-harness -- --focused-phase4 --submit-strategy current --allow-failures --out docs/cencon/proof/issue-1119
```

Submit strategies:

- `current`: the product path for each route.
- `bracketed-paste`: harness-only raw PTY paste, gated on observed mode 2004. Routes that cannot
  express raw PTY input record `not_applicable`.

Use `--allow-failures` for comparison runs where failing cells are expected evidence rather than a
failed harness execution.

Phase 4:

- By default the harness probes every built-in agent plugin and records missing tools as skipped
  with a reason.
- `--focused-phase4` applies the issue #1119 run-count policy: 5 runs for the broad matrix and 25
  runs for Codex REST/fleet plus Claude Code large-line/multiline combinations.
- The report includes a flake-rate matrix grouped by agent, case, route, and strategy. A combination
  only passes the gate at 100% success.

The harness uses a throwaway repository under the output directory unless `--repo` is supplied.
Do not point `--repo` at the devthrottle working tree.
