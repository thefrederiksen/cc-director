# Terminal Injection QA Report

Date: 2026-07-07
Branch: issue-1119-terminal-inject-all-agents-flake-rate
Harness output: artifacts/terminal-inject-final-hard-matrix-run3

## Scope

Final live proof run covered the hard input set across all public submit routes:

- Cases: medium-line, large-line, multiline
- Routes: direct, REST, fleet, voice-turn
- Strategy: current product submit path
- Runs: 1 per agent/case/route combination

## Result

Installed agents passed every applicable hard combination.

| Agent | Version | Result |
| --- | --- | --- |
| Claude Code | 2.1.202 | 12 passed, 0 failed |
| Pi | 0.79.4 | 12 passed, 0 failed |
| Codex | codex-cli 0.142.5 | 12 passed, 0 failed |
| Gemini | 0.1.11 | 12 passed, 0 failed |
| OpenCode | 1.15.12 | 12 passed, 0 failed |
| Grok | 0.2.87 stable | 12 passed, 0 failed |
| Copilot | GitHub Copilot CLI 1.0.68 | 12 passed, 0 failed |
| Cursor | not installed | 12 skipped |

Overall: 84 passed, 0 failed, 12 skipped, 96 total.

## Proof Artifacts

- Summary JSON: `docs/cencon/proof/issue-1119/final-hard-matrix-summary.json`
- Summary HTML: `docs/cencon/proof/issue-1119/final-hard-matrix-summary.html`
- Raw run directory: `artifacts/terminal-inject-final-hard-matrix-run3`

## Notes

- Cursor was skipped because `cursor-agent` is not installed on this machine.
- The final run started at `2026-07-07T08:52:57Z` and finished at `2026-07-07T09:17:05Z`.
- The final fixes were validated against live CLIs, not mocks.
