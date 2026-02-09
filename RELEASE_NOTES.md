# CC Director Release Notes

## v1.0.0 — Session Persistence & Daily-Driver Release

First production release. CC Director can now be closed and reopened without
losing running Claude Code sessions.

### Session Persistence
- **Detach on close** — closing Director with active sessions shows a custom
  dialog. By default sessions are kept alive as standalone console windows.
- **Reattach on startup** — persisted sessions are restored from
  `~/Documents/CcDirector/sessions.json` and their console windows are
  recaptured as overlays.
- **SessionStateStore** — new JSON-backed store for session metadata (PIDs,
  HWNDs, activity state, Claude session IDs).

### Embedded Console Host
- `Detach()` restores console window borders, taskbar presence, and owner
  so it becomes a normal standalone window.
- `Reattach()` static factory recaptures an existing console process by PID
  and persisted HWND, strips borders, and wires exit events.
- `DetachAll()` companion to `DisposeAll()` for the keep-alive path.

### Single-Instance Enforcement
- Global mutex prevents two Director instances from fighting over the same
  console windows.

### UI Improvements
- Custom dark-themed close dialog replaces the Windows MessageBox, with a
  "Shut down command windows" checkbox (unchecked by default).
- Prompt input text box is now 4 lines tall by default for easier editing.
- Prompt bar is aligned with the terminal area — left and right sidebars
  extend full height to the bottom of the window.

### Prior Releases

#### v0.2.0 — Session Switching Without Restart
#### v0.1.0 — Working Pre-Release
