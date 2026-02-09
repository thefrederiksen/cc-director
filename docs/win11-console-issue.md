# CC Director: Windows 11 Console Window Capture Issue

## Problem Summary

On Windows 11 machines where Windows Terminal is the default terminal application, the CC Director's Embedded mode fails to capture and overlay the console window. Claude Code launches and runs correctly, but appears in a **separate Windows Terminal window** instead of inside the Director's terminal panel.

## Root Cause

The Embedded mode works by:

1. Launching `claude.exe` with `CreateNoWindow = false` (so it gets a visible console)
2. Calling `AttachConsole(pid)` + `GetConsoleWindow()` to find the console window HWND
3. Stripping the window's borders and positioning it as an overlay inside the WPF app

**On Windows 11 with Windows Terminal as the default terminal**, step 2 breaks:

- Windows intercepts console process creation and delegates it to Windows Terminal via `OpenConsole.exe`
- `GetConsoleWindow()` returns a **`PseudoConsoleWindow`** — a zero-size invisible stub window at (0,0)
- The actual rendering is done by Windows Terminal in its own `CASCADIA_HOSTING_WINDOW_CLASS` window
- The stub HWND is useless for repositioning, resizing, or overlaying

## Evidence from Logs

```
[EmbeddedConsoleHost] Console window discovered:
  HWND=0x2F10F4, Class="PseudoConsoleWindow", OwnerPID=3100
  Rect=(0,0)-(0,0) [0x0]
  Style=0x94000000, ExStyle=0x08000080
```

Key observations:
- Window class is `PseudoConsoleWindow` (not `ConsoleWindowClass` which is the legacy conhost window)
- Dimensions are 0x0 — it's an invisible stub
- `WindowsTerminal.exe` (PID 29492) is running with a real window (HWND=0x20C20)
- Two `OpenConsole.exe` processes are present — these are the WT delegation bridge

## Windows Terminal Delegation Architecture

When WT is the default terminal:

```
WindowsTerminal.exe (visible CASCADIA_HOSTING_WINDOW_CLASS window)
  └─ OpenConsole.exe (bridge — holds the pseudo console)
       └─ claude.exe (our child process)
            └─ PseudoConsoleWindow (0x0 stub returned by GetConsoleWindow)
```

The registry key `HKCU\Console\%%Startup` controls delegation:
- `DelegationConsole` / `DelegationTerminal` GUIDs determine which host is used
- On this machine they show `(not set)`, but Windows 11 still defaults to WT when it's installed
- Setting both to `{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}` would force legacy conhost

## What Doesn't Work

| Approach | Result |
|----------|--------|
| `GetConsoleWindow()` | Returns 0x0 `PseudoConsoleWindow` stub |
| `Process.MainWindowHandle` | Returns 0x0 for console apps |
| `conhost.exe claude.exe` | Still delegated to WT (undocumented, unreliable) |
| ConPTY mode | Had other issues in this project — not viable currently |

## Possible Solutions to Investigate

### 1. Walk the process tree to find the Windows Terminal window

Find the actual WT window hosting our session:

1. From `claude.exe` PID, walk up the parent chain to find `OpenConsole.exe`
2. From `OpenConsole.exe`, find parent `WindowsTerminal.exe`
3. Use `EnumWindows` to find the `CASCADIA_HOSTING_WINDOW_CLASS` window belonging to that WT PID
4. Capture/overlay that window instead

**Caveat**: WT hosts multiple tabs in one window. Controlling just one tab's area may not be feasible. The WT team warns this class name is internal and may change.

### 2. Force legacy conhost via registry (per-launch)

Temporarily set `HKCU\Console\%%Startup\DelegationConsole` and `DelegationTerminal` to the conhost GUID before launching, then restore after:

```
{B23D10C0-E52E-411E-9D5B-C09FDF709C7D}
```

**Caveat**: Race condition with other console launches happening simultaneously.

### 3. Use `CREATE_NEW_CONSOLE` with `STARTUPINFO` that specifies a title, then `FindWindow`

Launch the process with a unique window title, then use `FindWindow(null, uniqueTitle)` to locate it.

**Caveat**: Only works if WT doesn't intercept the creation. May still get the stub.

### 4. Fix the ConPTY mode issues

ConPTY bypasses WT delegation entirely because the app becomes the terminal host. The CC Director already has a full ConPTY implementation (`ProcessHost.cs`, `PseudoConsole.cs`) and terminal renderer (`TerminalControl.cs`, `AnsiParser.cs`). Fixing whatever issues existed in ConPTY mode would be the most robust solution.

### 5. Detect WT and prompt the user to change default terminal

Check the registry on startup. If WT delegation is detected, show a message suggesting the user change their default terminal to "Windows Console Host" in Windows Settings > Terminal.

## Relevant Files

| File | Purpose |
|------|---------|
| `src/CcDirector.Wpf/Controls/EmbeddedConsoleHost.cs` | Console window capture and overlay logic |
| `src/CcDirector.Wpf/MainWindow.xaml.cs` | Session creation (`CreateEmbeddedSession`) |
| `src/CcDirector.Core/Sessions/SessionManager.cs` | Session factory methods for all 3 modes |
| `src/CcDirector.Core/ConPty/ProcessHost.cs` | ConPTY process launcher (existing, unused) |
| `src/CcDirector.Core/ConPty/PseudoConsole.cs` | ConPTY creation (existing, unused) |
| `src/CcDirector.Wpf/Controls/TerminalControl.cs` | WPF terminal renderer for ConPTY mode |
| `src/CcDirector.Wpf/Helpers/AnsiParser.cs` | VT100/ANSI sequence parser |
| `src/CcDirector.Core/Utilities/FileLog.cs` | File logger (`%LOCALAPPDATA%\CcDirector\logs\`) |

## Relevant Links

- [Windows Terminal default terminal delegation](https://devblogs.microsoft.com/commandline/windows-terminal-as-your-default-command-line-experience/)
- [PseudoConsoleWindow 0x0 issue (terminal #13525)](https://github.com/microsoft/terminal/issues/13525)
- [ShowWindow doesn't work with WT (terminal #15311)](https://github.com/microsoft/terminal/issues/15311)
- [Finding CASCADIA_HOSTING_WINDOW_CLASS (terminal #14492)](https://github.com/microsoft/terminal/discussions/14492)
- [Creating a Pseudoconsole session (MS docs)](https://learn.microsoft.com/en-us/windows/console/creating-a-pseudoconsole-session)
