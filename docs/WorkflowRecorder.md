# Workflow Recorder

Record browser actions in real-time, save them as named workflows per connection, and replay them later.

---

## Why

Browser automation with cc-browser is powerful but stateless -- each command runs independently. Many real-world tasks are sequences: log in, navigate to a page, fill a form, submit. Today the only way to repeat these sequences is to script them manually or re-issue each command.

The Workflow Recorder solves this by capturing actions as they happen in the browser and saving them as replayable JSON files. This enables:

- **Repeatable tasks** -- Save a multi-step browser workflow once, replay it anytime
- **Visual debugging** -- See exactly what actions cc-browser is executing in real-time
- **Workflow library** -- Build a collection of named workflows per connection (e.g., "post-article", "send-messages", "check-analytics")
- **No scripting required** -- Record by doing, not by writing code

---

## How It Works

### Architecture

```
+---------------------+          HTTP polling          +------------------+
| WorkflowRecorder    | <---------------------------- | cc-browser       |
| (WPF window,        |    GET /history?connection=    | daemon           |
|  left 20% screen)   |       &since=<timestamp>       | (port 9280)     |
+---------------------+                                +------------------+
                                                              |
                                                              | action history
                                                              | (in-memory)
                                                              |
                                                        +------------------+
                                                        | Brave browser    |
                                                        | (right 80%      |
                                                        |  screen)         |
                                                        +------------------+
```

The recorder is a WPF window that polls the cc-browser daemon's `/history` endpoint every second. The daemon already logs every POST command (navigate, click, type, etc.) to an in-memory action history. The recorder filters by connection name and timestamp to show only actions from the current recording session.

### Daemon Changes

Two changes to `tools/cc-browser/src/daemon.mjs`:

1. **Action history now stores `params`** -- Each history entry includes the full action parameters (url, text, selector, value, etc.), not just the command name. This lets the recorder display meaningful context like "click -> text: Submit" instead of just "click".

2. **`GET /history` supports filtering** -- New query parameters:
   - `?connection=<name>` -- Filter by connection
   - `?since=<ISO timestamp>` -- Only actions after this time
   - `?limit=<N>` -- Max results (default 100)

### Chrome Launch Changes

`tools/cc-browser/src/chrome-launch.mjs` and the daemon's `POST /connections/open` now accept `windowPosition` and `windowSize` options, mapped to Chromium's `--window-position` and `--window-size` flags. This allows controlled placement of the browser window.

---

## User Flow

1. Open CC Director, go to the **Connections** tab
2. Click the **Workflow** button on a connection card
3. A confirmation dialog appears:
   - Explains the recorder needs exclusive browser control
   - Asks the user to close all existing Brave windows
4. Click **Start**:
   - If the connection is currently open, it is closed first
   - The **recorder window** opens on the left 20% of the screen
   - A **fresh Brave instance** launches on the right 80%
   - The browser is positioned via Chromium flags and reinforced via Win32 `SetWindowPos`
5. Click **Record** in the recorder panel
6. Interact with the browser -- navigate, click, type
7. Actions appear in the recorder's action log in real-time (1-second polling)
8. Click **Stop** when done
9. Enter a name and click **Save** -- workflow is saved as JSON
10. To replay: select a saved workflow from the list, click **Replay**

---

## Recorder Window Layout

```
+-- WORKFLOW: {connection} -----------+
| Status: IDLE / RECORDING / REPLAYING |
|                                      |
| [Record] [Stop] [Clear]             |
|                                      |
| -- Action Log ---------------------- |
| 09:14:01 navigate                    |
|    -> url: https://medium.com        |
| 09:14:03 click                       |
|    -> text: "New story"              |
| 09:14:05 type                        |
|    -> value: "My Article Title"      |
|                                      |
| SAVED WORKFLOWS                      |
| > post-article (4 actions)           |
| > send-messages (12 actions)         |
|                                      |
| [workflow-name___] [Save] [Replay]   |
|                                      |
| Actions: 4  |  Elapsed: 7s          |
+--------------------------------------+
```

---

## Storage

Workflows are saved as JSON files per connection:

```
%LOCALAPPDATA%/cc-director/connections/{connection-name}/workflows/{workflow-name}.json
```

### JSON Format

```json
{
  "name": "post-article",
  "connection": "medium",
  "createdAt": "2026-03-08T14:30:00.000Z",
  "actions": [
    { "command": "navigate", "params": { "url": "https://medium.com" } },
    { "command": "click", "params": { "text": "New story" } },
    { "command": "type", "params": { "value": "My Article Title" } }
  ]
}
```

---

## Replay

Replay sends actions to the daemon's `POST /batch` endpoint with `stopOnError: true`. Large workflows are chunked into batches of 50 commands. The batch endpoint executes commands sequentially against the browser, using the same connection.

---

## Files Changed

| File | Change |
|------|--------|
| `tools/cc-browser/src/daemon.mjs` | Store params in history, add connection/since/limit filtering to GET /history |
| `tools/cc-browser/src/chrome-launch.mjs` | Add windowPosition/windowSize Chromium flags |
| `src/CcDirector.Core/Storage/CcStorage.cs` | Add `ConnectionWorkflows()` path method |
| `src/CcDirector.Wpf/Controls/ConnectionsView.xaml` | Add Workflow button to connection cards |
| `src/CcDirector.Wpf/Controls/ConnectionsView.xaml.cs` | Workflow click handler, OpenConnectionPositioned, RepositionBrowserWindow, Brave paths in FindChromePath, Win32 SetWindowPos |
| `src/CcDirector.Wpf/WorkflowConfirmDialog.xaml(.cs)` | Confirmation dialog before starting |
| `src/CcDirector.Wpf/WorkflowRecorderWindow.xaml(.cs)` | Recorder panel: Record/Stop/Save/Load/Replay |

---

## Limitations

- **Recording captures cc-browser daemon commands only** -- Direct keyboard/mouse interaction in the browser (without going through the daemon) is not recorded. The extension relays actions to the daemon, which logs them.
- **Replay timing** -- Replayed actions execute as fast as the browser allows; original timing between actions is not preserved.
- **Single monitor** -- The 80/20 split assumes a single primary monitor.
- **Brave required** -- Chrome stable (130+) blocks `--load-extension`, so Brave is the preferred browser.
