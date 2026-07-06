# Cockpit

The Cockpit is the single UI for driving every Claude session on the tailnet: it reaches dumb, long-lived Director "runners" over the network so sessions never have to be killed.

> **Rebuild complete (epic #967, cutover issue #979).** The Cockpit is now a **React single-page app** (`apps/cockpit`) served in-process by the Gateway at the site root `/`, thin over the shared TypeScript library `@devthrottle/client-core`. The original **Blazor Server** Cockpit (`src/CcDirector.Cockpit`) and its separate supervised process were **retired** in the #979 cutover. The documents below that describe the Blazor design are kept as **historical** design record; the authoritative current design is [COCKPIT_REACT_REBUILD.md](COCKPIT_REACT_REBUILD.md).

| Document | Status | Covers |
|---|---|---|
| [COCKPIT_REACT_REBUILD.md](COCKPIT_REACT_REBUILD.md) | CURRENT | The React rebuild design and rationale (supersedes the Blazor design); realized by epic #967 / #979 |
| [COCKPIT_DESIGN.md](COCKPIT_DESIGN.md) | HISTORICAL (Blazor) | The original Blazor Server design: the idea, the driver, v1 scope, the connection model, where the smarts run, project shape, hosting, build order |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | HISTORICAL (Blazor) | Status snapshot of the built Blazor MVP + the ordered phase plan |
| [HANDOVER.md](HANDOVER.md) | HISTORICAL (Blazor) | Hand-off for a fresh session on the Blazor Cockpit: status, the launch gate, and cross-machine rollout |
| [BUILD_CHECKLIST.md](BUILD_CHECKLIST.md) | HISTORICAL (Blazor) | The Director build checklist for the Blazor MVP |
| `cockpit-topology.d2` / `.png` / `.svg` | CURRENT | Fleet topology: outside access layer (the Gateway front door serving the Cockpit) reaching into the Tailscale network of Director runners |

**Read order for someone new:**
1. `cockpit-topology.png` - see the three lanes
2. `COCKPIT_REACT_REBUILD.md` - the current (React) design
3. `COCKPIT_DESIGN.md` - the historical Blazor design, for background only

## See also

- `../gateway/` - the Gateway/Director split the Cockpit builds on (and the shipped fleet-wide `GET /sessions` it consumes)
- `../wingman/SESSION_VIEW_MERGE_PLAN.md` - the wingman/agent-feed view the Cockpit replaces
- `tools/harnesses/wingman-briefing/` - the working prototype (cockpit.html + server.py + PLAN-v1-cockpit.md)

## Re-rendering the diagram

```powershell
& "D:\Tools\d2\d2.exe" --theme=0 --layout=elk cockpit-topology.d2 cockpit-topology.png
```
