# Phase 1 Audit: Session Mode Analysis

## Overview

The cc_director application supports three session modes. This document maps what code is specific to each mode and identifies what can be abstracted behind a common interface.

---

## Session Mode Comparison Matrix

### Core Features

| Feature | ConPty | Embedded | Pipe |
|---------|--------|----------|------|
| Process ownership | ProcessHost owns process | EmbeddedConsoleHost owns process | Session spawns per-prompt |
| Terminal buffer | CircularTerminalBuffer (2MB) | Minimal 1-byte buffer (unused) | CircularTerminalBuffer (2MB) |
| Display rendering | WPF TerminalControl | Real console window overlay | WPF TerminalControl |
| Input method | Write to PTY pipe | WriteConsoleInput or clipboard | Write to stdin pipe |
| Resize support | PseudoConsole.Resize() | N/A (console auto-sizes) | N/A |
| Session persistence | No (PTY dies with app) | Yes (process survives, reattach) | No (stateless) |
| Process reattach | Not possible | Supported | Not applicable |

### Session.cs Mode-Specific Code

| Method/Property | ConPty | Embedded | Pipe |
|-----------------|--------|----------|------|
| Constructor | Lines 84-103 | Lines 129-144, 147-172 | Lines 106-126 |
| `ProcessId` | `_processHost.ProcessId` | `EmbeddedProcessId` | `_currentProcess?.Id` |
| `SendInput()` | Writes to processHost | No-op (returns) | No-op (returns) |
| `SendTextAsync()` | Write + delay + Enter | No-op (handled elsewhere) | Spawns new process |
| `SendText()` | Write + Enter | No-op | No-op |
| `Resize()` | `_console.Resize()` | No-op | No-op |
| `KillAsync()` | `GracefulShutdownAsync()` | Sets status only | Kills `_currentProcess` |
| `Dispose()` | Disposes processHost | No-op (process external) | Kills + disposes process |

### SessionManager.cs Creation Methods

| Method | Creates | Process Lifecycle |
|--------|---------|-------------------|
| `CreateSession()` | PseudoConsole + ProcessHost + buffer | Immediate spawn |
| `CreateEmbeddedSession()` | Minimal Session only | WPF layer spawns later |
| `CreatePipeModeSession()` | Buffer only | Deferred (per-prompt spawn) |

### Persistence (Embedded Only)

| Method | Purpose |
|--------|---------|
| `SaveCurrentState()` | Save running embedded sessions |
| `SaveSessionState()` | Save with HWND from WPF layer |
| `LoadPersistedSessions()` | Restore + validate PIDs |
| `RestoreEmbeddedSession()` | Create Session from persisted data |

---

## EmbeddedConsoleHost Workarounds

These are specific to Embedded mode and NOT needed for ConPty:

### 1. TOPMOST Flash (Z-Order)
- **Location:** Lines 527-534, 555-563
- **Problem:** WPF activation can race with console Z-order
- **Solution:** Briefly set TOPMOST, then revert to NOTOPMOST
- **ConPty needs this?** NO - no separate window

### 2. Border Stripping
- **Location:** Lines 227-243
- **Problem:** Console window has title bar, borders, taskbar icon
- **Solution:** Remove WS_CAPTION, WS_THICKFRAME, set WS_EX_TOOLWINDOW
- **ConPty needs this?** NO - renders in WPF

### 3. Two-Tier Text Input
- **Location:** Lines 276-482
- **Problem:** Some consoles don't support WriteConsoleInput
- **Solution:** Tier 1 = WriteConsoleInput, Tier 2 = clipboard paste
- **ConPty needs this?** NO - writes directly to PTY pipe

### 4. Console Window Discovery
- **Location:** Lines 630-678
- **Problem:** Console HWND not immediately available after spawn
- **Solution:** AttachConsole polling with 5s timeout
- **ConPty needs this?** NO - no console window to find

### 5. Console Font Setting
- **Location:** Lines 183-222
- **Problem:** Default console font may be ugly/small
- **Solution:** SetCurrentConsoleFontEx to Consolas
- **ConPty needs this?** NO - WPF uses its own fonts

### 6. Default Terminal Detection
- **Location:** Lines 720-774
- **Problem:** Windows Terminal intercepts console creation
- **Solution:** Check registry for delegation settings
- **ConPty needs this?** NO - doesn't spawn visible console

### 7. Detach/Reattach
- **Location:** Lines 47-100, 586-602
- **Problem:** Need to restore console on UI restart
- **Solution:** RestoreBorders(), clear owner, reattach by PID
- **ConPty needs this?** NO - can't reattach to PTY

---

## Shared Infrastructure

These components work across all modes:

| Component | Used By | Notes |
|-----------|---------|-------|
| `CircularTerminalBuffer` | ConPty, Pipe | Embedded uses 1-byte dummy |
| `ActivityState` | All | Driven by hook events |
| `PipeMessage` / `EventRouter` | All | Named pipe communication |
| `SessionStateStore` | Embedded only | Persistence layer |

---

## Proposed Interface Abstraction

Based on this audit, here's what `ISessionBackend` should expose:

```csharp
public interface ISessionBackend : IDisposable
{
    // Identity
    int ProcessId { get; }
    string Status { get; }
    bool IsRunning { get; }
    bool HasExited { get; }

    // Events
    event Action<string>? StatusChanged;
    event Action<int>? ProcessExited;

    // Lifecycle
    void Start(string executable, string args, string workingDir, short cols, short rows);
    Task GracefulShutdownAsync(int timeoutMs);

    // I/O
    void Write(byte[] data);
    void WriteText(string text); // Convenience: UTF8 encode + optional Enter
    CircularTerminalBuffer? Buffer { get; } // Null for Embedded

    // Terminal (ConPty only)
    void Resize(short cols, short rows);
}
```

### Backend Implementations

| Backend | Owns | Notes |
|---------|------|-------|
| `ConPtyBackend` | PseudoConsole, ProcessHost, Buffer | Clean PTY implementation |
| `EmbeddedBackend` | EmbeddedConsoleHost reference | Wraps existing workarounds |
| `PipeBackend` | Per-prompt Process, Buffer | Stateless spawning |

---

## Files to Modify in Phase 2

### New Files
```
src/CcDirector.Core/Backends/ISessionBackend.cs
src/CcDirector.Core/Backends/ConPtyBackend.cs
src/CcDirector.Core/Backends/PipeBackend.cs
src/CcDirector.Wpf/Backends/EmbeddedBackend.cs  (WPF project due to EmbeddedConsoleHost dependency)
```

### Modified Files
```
src/CcDirector.Core/Sessions/Session.cs         - Hold ISessionBackend instead of direct references
src/CcDirector.Core/Sessions/SessionManager.cs  - Factory methods create backends
```

---

## Key Findings

1. **ConPty mode is cleanest** - no workarounds needed, direct PTY I/O
2. **Embedded mode has 7 workarounds** - all due to managing a real console window
3. **Pipe mode is simplest** - stateless, spawns process per prompt
4. **Session class mixes concerns** - mode-specific logic should move to backends
5. **Test app's ClaudeSession** - good model for ConPtyBackend encapsulation
6. **ConPty classes are shared** - ProcessHost, PseudoConsole identical between main and test app

---

## Recommendation

Proceed to Phase 2: Extract `ISessionBackend` interface. The audit confirms the modes are cleanly separable - each has distinct process ownership, I/O patterns, and lifecycle management.
