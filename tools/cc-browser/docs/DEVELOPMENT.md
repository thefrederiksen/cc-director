# cc-browser Development Reference

## Build and Deploy

Run `build.ps1` to deploy extension and source files:

```powershell
powershell -ExecutionPolicy Bypass -File tools/cc-browser/build.ps1
```

This copies source files to `%LOCALAPPDATA%\cc-director\bin\_cc-browser\`, including the
browser extension in the `extension/` subdirectory.

### Pre-build process cleanup

The build script automatically kills running cc-browser daemon and native-host node
processes before deploying. This prevents "file in use" errors when replacing files.

It uses `Get-CimInstance Win32_Process` to find node processes by CommandLine (not Path,
since node.exe Path always points to the nodejs install dir, not the script being run).

### Post-build service worker cleanup

After deploying, the build clears Chrome/Brave service worker caches for all connections.
This is non-fatal (`-ErrorAction SilentlyContinue`) -- if a browser is still open, the
cache clear is skipped for that connection and will take effect on the next close/open cycle.

## Chrome/Brave Service Worker Caching

Chrome and Brave cache extension service workers in `Default/Service Worker/ScriptCache/`
inside each connection's user-data-dir (`%LOCALAPPDATA%\cc-director\connections\<name>\`).

**Key gotcha:** A browser restart does NOT clear this cache. Updated `background.js` code
will NOT take effect until the cached service worker is deleted.

### Automatic clearing

`build.ps1` automatically deletes the `Default/Service Worker/` directory for every
connection after deploying new code. This ensures the next browser launch loads the
updated extension.

### Manual clearing

If you need to force-reload without a full build:

1. Close the connection: `cc-browser connections close <name>`
2. Delete the cache:
   ```
   rm -rf "%LOCALAPPDATA%/cc-director/connections/<name>/Default/Service Worker/"
   ```
3. Reopen: `cc-browser connections open <name>`

## Session and Tab Management

### No tab accumulation

Chrome stores open tabs in session files (`Default/Sessions/`, `Default/Current Session`,
`Default/Current Tabs`, `Default/Last Session`, `Default/Last Tabs`). On startup with
"Continue where you left off" enabled, Chrome restores all previous tabs, causing tabs to
accumulate across open/close cycles.

**Fix:** `chrome-launch.mjs` deletes all session restore files before each launch. The
connection's target URL is passed as a Chrome launch argument, so the browser opens with
a single tab at the right destination. No session restore needed.

### No flashing windows on close

`killChromeForConnection()` uses `windowsHide: true` on all child process exec calls
to prevent visible console windows. It first tries a graceful kill via stored PID
(from `cc-browser.json`), waits up to 5s, then force-kills with `/T` (tree kill).
Fallback: PowerShell scan for processes matching the profile directory.

### Daemon tab cleanup (safety net)

The daemon registers an `onConnect` callback that waits 2s after a connection is
established, then closes all non-active tabs. This is a safety net in case session
files were not fully cleaned (e.g., browser was still writing when files were deleted).
