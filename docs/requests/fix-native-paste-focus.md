# Request: Fix Native Paste -- Browser Window Must Have OS Focus

## The Problem

`cc-browser paste --method native` writes text to the clipboard and sends Ctrl+V using the `keysender` library. But `keysender` sends the keystroke to **whatever window currently has OS-level focus**. If the browser is not the foreground window, the paste goes nowhere.

This worked once during a previous test because the browser happened to have focus. It fails every other time because the user's terminal/editor has focus instead.

## The Root Cause

The current code at `%LOCALAPPDATA%/cc-director/bin/_cc-browser/src/daemon.mjs` line 621-628:

```javascript
if (body.method === 'native') {
  clipboardy.writeSync(text);
  const hw = new keysender.Hardware();
  hw.keyboard.toggleKey('ctrl', true);
  hw.keyboard.sendKey('v');
  hw.keyboard.toggleKey('ctrl', false);
  jsonSuccess(res, { pasted: true, length: text.length, method: 'native' });
  return;
}
```

**Missing step: bring the browser window to the foreground before sending Ctrl+V.**

## The Fix

Before sending the keystroke, activate the browser window using its PID. The PID is stored in `{profileDir}/cc-browser.json` (e.g., `%LOCALAPPDATA%/cc-director/connections/linkedin/cc-browser.json`).

`keysender` itself can target a specific window. Check if `keysender.Hardware` accepts a window handle or process ID. If not, use Windows API to bring the window to foreground first.

### Option A: keysender window targeting

```javascript
// keysender can target a specific window by title or handle
const hw = new keysender.Hardware(browserPid);
// or
const hw = new keysender.Hardware("LinkedIn");
```

Check keysender docs for exact API.

### Option B: Win32 SetForegroundWindow via ffi or PowerShell

```javascript
import { execSync } from 'child_process';

// Read browser PID from connection config
const configPath = join(getProfileDir(connectionName), 'cc-browser.json');
const config = JSON.parse(readFileSync(configPath, 'utf8'));
const pid = config.chromePid;

// Bring browser to foreground
execSync(`powershell -NoProfile -Command "[Microsoft.VisualBasic.Interaction]::AppActivate(${pid})"`, {
  windowsHide: true,
  timeout: 3000,
});

// Wait for window to come to foreground
await new Promise(r => setTimeout(r, 300));

// Now send Ctrl+V -- it will go to the browser
clipboardy.writeSync(text);
const hw = new keysender.Hardware();
hw.keyboard.toggleKey('ctrl', true);
hw.keyboard.sendKey('v');
hw.keyboard.toggleKey('ctrl', false);
```

### Option C: Python script (pyautogui + pyperclip)

Create `tools/cc-browser/src/native-paste.py`:

```python
import sys
import subprocess
import time

text = sys.stdin.read() if '--stdin' in sys.argv else sys.argv[1]
pid = int(sys.argv[sys.argv.index('--pid') + 1])

# Bring window to foreground
subprocess.run([
    'powershell', '-NoProfile', '-Command',
    f'[Microsoft.VisualBasic.Interaction]::AppActivate({pid})'
], check=True)

time.sleep(0.3)

# Clipboard + Ctrl+V
import pyperclip
import pyautogui
pyperclip.copy(text)
pyautogui.hotkey('ctrl', 'v')
```

## The Full Sequence That Must Work

1. `cc-browser click --ref <textarea-ref>` -- gives DOM focus to the textarea
2. `sleep 2` -- wait for focus to settle
3. `cc-browser paste --method native "text"` -- this command must:
   a. Write text to OS clipboard
   b. Bring the browser window to the OS foreground
   c. Wait 300ms for window activation
   d. Send real Ctrl+V keystroke
   e. The keystroke lands in the textarea because it has DOM focus from step 1

Steps 1-3 must be chained in one command (`&&`) because any pause between them risks the user clicking elsewhere and stealing focus.

## Where to Find the Browser PID

```
%LOCALAPPDATA%/cc-director/connections/<connection-name>/cc-browser.json
```

Contains: `{ "connection": "linkedin", "daemonPort": 9280, "chromePid": 46692 }`

The daemon route has access to the connection name via `requireConnected(body)`. Use `getProfileDir(connectionName)` from `connections.mjs` to build the path.

## Testing

Test with at least 5 different scenarios:

1. Browser is in background, terminal is in foreground -- paste must bring browser forward and paste
2. Browser is already in foreground -- paste must work
3. Browser is minimized -- paste must restore and paste
4. Long text with special characters (quotes, newlines) -- must paste correctly
5. Two rapid pastes in sequence -- both must work

For each test:
- Open a text input on any website (e.g., Google search box, LinkedIn message box)
- Click the input via `cc-browser click --ref`
- Run `cc-browser paste --method native "test text"`
- Take screenshot
- Verify text appeared

## Files to Change

1. `tools/cc-browser/src/daemon.mjs` -- the native paste block (lines 621-628)
2. Copy updated file to `%LOCALAPPDATA%/cc-director/bin/_cc-browser/src/daemon.mjs`
3. Restart daemon after changes

## Do Not

- Do not use synthetic JavaScript events as a fallback
- Do not use execCommand
- Do not add workarounds
- Fix the root cause: bring the window to foreground, then send the real keystroke
