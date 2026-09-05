# Your Throttle conformance - soren, 2026-W35

Window: 2026-08-24T04:00:00+00:00 to 2026-08-31T04:00:00+00:00 (America/Toronto, Monday to Monday).

| figure | library (Gateway code over the hosted database) | mentor side (harness reader over its extract) |
|---|---:|---:|
| turns | 1786 | 1192 |
| voiceTurns | 1015 | 1015 |
| typedTurns | 771 | 177 |
| sessions | 129 | 118 |
| spoken share | 56.83% | 85.15% |
| bucket typed/desktop | 696 | 105 |
| bucket typed/phone | 68 | 65 |
| bucket typed/unknown | 7 | 7 |
| bucket voice/desktop | 835 | 835 |
| bucket voice/phone | 180 | 180 |
| excluded.noInputOrigin | 662 | 1256 |
| excluded.agentDriven | 0 | 0 |
| excluded.framework | 160 | 160 |
| excluded.unresolved | 502 | 1096 |

Per-agent and per-repository splits and the hourly series were compared against a plain reading of the extract; 5 agents, 13 repositories, 110 hours.

## FAIL - the consumers diverge

- turns: library=1786 mentor-side=1192
- typedTurns: library=771 mentor-side=177
- sessions: library=129 mentor-side=118
- bucket typed/desktop: library=696 mentor-side=105
- bucket typed/phone: library=68 mentor-side=65
- excluded.noInputOrigin: library=662 mentor-side=1256
- excluded.unresolved: library=502 mentor-side=1096

(run with --break-predicate: the mentor side deliberately dropped null-send-source rows)
