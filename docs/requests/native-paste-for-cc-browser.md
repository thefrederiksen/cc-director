# Request: Add Native Paste to cc-browser

## Problem

cc-browser's current `paste` command uses JavaScript-level methods (synthetic ClipboardEvent, execCommand) to insert text into web page elements. These methods silently fail on sites like LinkedIn -- the command reports success but no text appears in the textarea.

A real human pastes by pressing Ctrl+V, which goes through the OS clipboard. LinkedIn accepts this because it's indistinguishable from a real user action.

## What We Need

A Python utility that cc-browser's daemon can call to paste text like a human would:

1. **Write text to the system clipboard** (e.g., `pyperclip.copy(text)` or `win32clipboard`)
2. **Send a real Ctrl+V keystroke at the OS level** (e.g., `pyautogui.hotkey('ctrl', 'v')`)

That's it. Two operations. The browser element is already focused from a prior `cc-browser click --ref` command. The Python script just needs to put text on the clipboard and press Ctrl+V.

## How It Gets Called

The cc-browser daemon (Node.js, at `tools/cc-browser/src/daemon.mjs`) handles a `POST /paste` route. When the user passes `--method native`, the daemon should call this Python script instead of sending the command to the Chrome extension.

Example CLI usage:
```
cc-browser --connection linkedin click --ref e3
sleep 2
cc-browser --connection linkedin paste --method native "Hello, I'd like to connect."
```

## Python Script Requirements

- Location: `tools/cc-browser/src/native-paste.py`
- Takes text as a command-line argument (or via stdin for long/special-character text)
- Writes the text to the OS clipboard
- Sends Ctrl+V at the OS level
- Exits with code 0 on success, non-zero on failure
- Windows only (this runs on Windows 11)
- Dependencies: `pyperclip` and `pyautogui` (or `win32clipboard` + `ctypes` for zero-dependency)

Example:
```python
# Usage: python native-paste.py "text to paste"
# Or:    echo "text to paste" | python native-paste.py --stdin

import sys
import pyperclip
import pyautogui

text = sys.argv[1] if len(sys.argv) > 1 else sys.stdin.read()
pyperclip.copy(text)
pyautogui.hotkey('ctrl', 'v')
```

## Daemon Integration

In `tools/cc-browser/src/daemon.mjs`, the `POST /paste` route needs a branch for `method === 'native'`:

```javascript
if (body.method === 'native') {
  const { execSync } = require('child_process');
  const text = body.text || body.value;
  execSync(`python "${scriptPath}" --stdin`, {
    input: text,
    windowsHide: true,
    timeout: 10000,
  });
  return { pasted: true, length: text.length, method: 'native' };
}
```

## CLI Integration

In `tools/cc-browser/src/cli.mjs`, the paste case already passes `body.method = flags.method`. No CLI changes needed beyond what's already been added.

## Files to Modify

1. **CREATE** `tools/cc-browser/src/native-paste.py` -- the Python script
2. **MODIFY** `tools/cc-browser/src/daemon.mjs` -- call the Python script when `method === 'native'`
3. **MODIFY** `tools/cc-browser/src/cli.mjs` -- already done (passes `--method` flag)

## Current State

- The `--method` flag is already wired through the CLI to the daemon
- The daemon has a partial implementation using PowerShell (which doesn't work properly)
- That PowerShell code should be replaced with the Python script call

## Testing

1. Open LinkedIn in cc-browser: `cc-browser connections open linkedin`
2. Navigate to any profile
3. Click More -> Connect -> Add a note
4. Click the textarea: `cc-browser --connection linkedin click --ref <ref>`
5. Wait 2 seconds
6. Paste: `cc-browser --connection linkedin paste --method native "test message"`
7. Take screenshot to verify text appeared: `cc-browser --connection linkedin screenshot`
