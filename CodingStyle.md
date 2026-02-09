# CC Director Coding Style Guide

This document describes the coding patterns and style conventions used throughout the CC Director codebase. The guiding principles are **simplicity**, **maintainability**, and **explicit error handling**.

## Core Philosophy

1. **Simplicity over cleverness** - Code should be obvious, not clever
2. **Explicit over implicit** - Make behavior clear, don't hide it
3. **Fail fast** - Validate early, throw specific exceptions
4. **No fallbacks** - Fix root causes, don't add workarounds
5. **Testable by design** - Return result objects, use dependency injection where it helps
6. **Zero warnings** - Treat warnings as errors, fix them immediately

---

## 1. Warnings Are Errors

**All projects must treat warnings as errors.** Warnings are not suggestions - they indicate real problems that will bite you later.

### Project Configuration

Every project must include this in the `.csproj` file:

```xml
<PropertyGroup>
  <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
</PropertyGroup>
```

### Why Zero Warnings?

1. **Warnings become errors** - A nullable warning today is a `NullReferenceException` in production tomorrow
2. **Warning fatigue** - 100 warnings means nobody notices when #101 is actually critical
3. **Code quality** - Clean builds indicate clean code

### Common Warnings and How to Fix Them

#### CS8600, CS8601, CS8602, CS8603, CS8604 - Nullable Warnings

Fix them properly, don't suppress them.

```csharp
// BAD - suppressing the warning
var name = tenant!.Name;  // Don't use ! to hide the problem

// GOOD - handle the null case
if (tenant is null)
{
    throw new ArgumentNullException(nameof(tenant));
}
var name = tenant.Name;

// GOOD - for optional values, use null-conditional
var name = tenant?.Name ?? "Unknown";
```

#### CS8618 - Non-nullable property not initialized

```csharp
// BAD
public string ConnectionString { get; set; }  // Warning!

// GOOD - use required modifier
public required string ConnectionString { get; set; }

// GOOD - or initialize with default
public string ConnectionString { get; set; } = string.Empty;

// GOOD - or make nullable if truly optional
public string? ConnectionString { get; set; }
```

#### CS0168, CS0219 - Unused variables

```csharp
// BAD - unused variable
catch (Exception ex) { }  // Warning: ex is never used

// GOOD - use discard if you don't need it
catch (Exception) { }

// Or if you need to log it
catch (Exception ex)
{
    FileLog.Write($"[Context] Operation failed: {ex.Message}");
}
```

#### CS4014 - Not awaiting async method

```csharp
// BAD - fire and forget without intention
SaveAsync();  // Warning: not awaited

// GOOD - await it
await SaveAsync();

// GOOD - if truly fire-and-forget, make it explicit with discard
_ = SaveAsync();
```

### Never Suppress Without Justification

If you must suppress a warning, document why:

```csharp
// Acceptable - with justification
#pragma warning disable CS8618 // Set by framework before use
public string Name { get; set; }
#pragma warning restore CS8618

// NOT acceptable - hiding problems
#pragma warning disable CS8602
var x = thing.Property;  // Just fix the null check!
#pragma warning restore CS8602
```

---

## 2. Naming Conventions

### Classes
- **PascalCase** for all class names
- Descriptive names indicating purpose
- Suffix patterns:
  - `*Provider` for data retrieval: `GitStatusProvider`, `GitSyncStatusProvider`
  - `*Manager` for lifecycle management: `SessionManager`
  - `*Host` for hosted/embedded processes: `EmbeddedConsoleHost`, `ProcessHost`
  - `*Router` for message routing: `EventRouter`
  - `*Store` for persistence: `SessionStateStore`, `RecentSessionStore`
  - `*Installer` for setup operations: `HookInstaller`
  - `*Control` for WPF user controls: `GitChangesControl`, `TerminalControl`
  - `*ViewModel` for UI view models: `SessionViewModel`

### Methods
- **PascalCase**
- **Verb + Noun** pattern: `GetStatus()`, `CreateSession()`, `KillAllSessions()`
- Async methods: suffix with `Async`: `GetStatusAsync()`, `FetchAsync()`
- Boolean methods: prefix with `Is`, `Has`, `Can`: `IsMainBranch()`, `HasUpstream`

### Properties
- **PascalCase**
- No prefix or suffix
- Use auto-properties: `public string Name { get; set; }`

### Fields
- Private fields: **_camelCase** with underscore prefix
- Readonly when possible: `private readonly SessionManager _sessionManager;`
- Constants: **PascalCase**

```csharp
// Good
private readonly GitStatusProvider _provider = new();
private DispatcherTimer? _pollTimer;
private const int MaxRetries = 3;

// Bad
private GitStatusProvider provider;  // Missing underscore
private DispatcherTimer timer;       // Missing underscore and nullable
```

### Parameters and Local Variables
- **camelCase**
- Descriptive names: `repoPath`, `sessionId`, `exitCode`

---

## 3. File and Namespace Organization

### File-Scoped Namespaces
Always use file-scoped namespaces (C# 10+):

```csharp
// Good
namespace CcDirector.Core.Sessions;

public class SessionManager
{
}

// Bad - block-scoped namespace adds unnecessary indentation
namespace CcDirector.Core.Sessions
{
    public class SessionManager
    {
    }
}
```

### One Class Per File
- Each public class gets its own file
- File name matches class name exactly
- Exception: small related types can be in one file (e.g., a result class with its supporting types, tree node types in a control file)

### Folder Structure Mirrors Namespaces
```
CcDirector.Core/
    Sessions/
        Session.cs           -> namespace CcDirector.Core.Sessions
        SessionManager.cs    -> namespace CcDirector.Core.Sessions
    Git/
        GitStatusProvider.cs -> namespace CcDirector.Core.Git
    Hooks/
        HookInstaller.cs     -> namespace CcDirector.Core.Hooks
CcDirector.Wpf/
    Controls/
        GitChangesControl.xaml.cs -> namespace CcDirector.Wpf.Controls
```

---

## 4. Nullable Handling

### Enable Nullable Reference Types
All projects have nullable enabled. Handle nulls explicitly.

### Validation Patterns

```csharp
// For required string parameters
public void ProcessFile(string filePath)
{
    ArgumentException.ThrowIfNullOrEmpty(filePath);
    // ... rest of method
}

// For required object parameters
public void ProcessSession(Session session)
{
    ArgumentNullException.ThrowIfNull(session);
    // ... rest of method
}

// For optional parameters - check and return early
public async Task RefreshAsync()
{
    if (_repoPath is null) return;
    // ... rest of method
}
```

### Pattern Matching for Null Checks
Use `is null` and `is not null` instead of `== null`:

```csharp
// Good
if (tenant is null) return;
if (result is not null) ProcessResult(result);

// Bad
if (tenant == null)  // Less clear intent
```

### Null-Conditional and Null-Coalescing

```csharp
// Null-conditional for optional operations
_timer?.Stop();
Application.Current?.Dispatcher.BeginInvoke(() => { });

// Null-coalescing for defaults
var displayName = session?.CustomName ?? "(none)";

// Null-coalescing assignment
_cache ??= new Dictionary<string, object>();
```

---

## 5. Error Handling

### No Fallback Programming
**Never add fallback logic.** If something might fail, fix the root cause or fail explicitly.

```csharp
// BAD - fallback hides problems
public Session GetSession(Guid id)
{
    try { return _sessions[id]; }
    catch { return new Session(); }  // NO! Hides the real problem
}

// GOOD - fail explicitly with clear error
public Session GetSession(Guid id)
{
    if (!_sessions.TryGetValue(id, out var session))
    {
        throw new KeyNotFoundException($"Session {id} not found");
    }
    return session;
}
```

### Try-Catch Strategy
- Wrap boundary methods in try-catch (event handlers, timer ticks, pipe message handlers)
- Log errors with full context via FileLog
- Return error results or swallow only where safe - don't swallow exceptions silently without a comment

```csharp
public async Task<GitStatusResult> GetStatusAsync(string repoPath)
{
    try
    {
        // ... execution logic
        return new GitStatusResult { Success = true, StagedChanges = staged };
    }
    catch (Exception ex)
    {
        return new GitStatusResult { Success = false, Error = ex.Message };
    }
}
```

### Result Objects
For operations that can fail, use result objects instead of exceptions for expected failures:

```csharp
public class GitStatusResult
{
    public List<GitFileEntry> StagedChanges { get; init; } = new();
    public List<GitFileEntry> UnstagedChanges { get; init; } = new();
    public bool Success { get; init; }
    public string? Error { get; init; }
}
```

---

## 6. Static vs Instance Methods

### When to Use Static Methods
Use static methods when:
- No instance state is needed
- The method is a pure function (same input = same output)
- Building utility/helper classes

```csharp
// Good - stateless utility
public static class HookRelayScript
{
    public static void EnsureWritten() { /* ... */ }
}
```

### When to Use Instance Methods
Use instance methods when:
- The class maintains state between calls
- Dependencies need to be injected
- Resource lifecycle management is needed

```csharp
// Good - maintains state
public class SessionManager : IDisposable
{
    private readonly Dictionary<Guid, Session> _sessions = new();

    public Session CreateSession(string repoPath) { /* ... */ }
}
```

### When to Use Dependency Injection
DI is **not** always the answer. This is a desktop app without a DI container, so prefer constructor parameters and composition.

**Use constructor parameters when:**
- Service needs collaborators or configuration
- You need to swap implementations for testing

**Use static methods when:**
- Operations are stateless
- Methods are pure functions

---

## 7. Method Design

### Keep Methods Small
- Target: under 30 lines
- Maximum: 50 lines (with good reason)
- If longer, extract private helper methods

### Single Responsibility
Each method should do one thing.

### Early Returns
Return early to reduce nesting:

```csharp
// Good - early returns
public async Task RefreshAsync()
{
    if (_repoPath is null || !Directory.Exists(_repoPath)) return;

    var result = await _provider.GetStatusAsync(_repoPath);
    if (!result.Success) return;

    // ... main logic
}

// Bad - deep nesting
public async Task RefreshAsync()
{
    if (_repoPath is not null)
    {
        if (Directory.Exists(_repoPath))
        {
            var result = await _provider.GetStatusAsync(_repoPath);
            if (result.Success)
            {
                // ... main logic
            }
        }
    }
}
```

---

## 8. Class Design

### Single Responsibility Principle
Each class should have one reason to change:

```csharp
// Good - focused responsibilities
public class SessionManager { /* session lifecycle only */ }
public class EventRouter { /* message routing only */ }
public class GitStatusProvider { /* git status queries only */ }
```

### Prefer Composition Over Inheritance
Use inheritance sparingly, prefer composition:

```csharp
// Good - composition
public class EventRouter
{
    private readonly SessionManager _sessionManager;

    public EventRouter(SessionManager sessionManager, Action<string> log) { /* ... */ }
}
```

### Access Control
Use `internal` for types and constructors that should only be created by specific classes:

```csharp
// Session constructor is internal - only SessionManager creates sessions
public class Session
{
    internal Session(Guid id, string repoPath) { /* ... */ }
}
```

---

## 9. Async/Await

### Naming
Always suffix async methods with `Async`:

```csharp
public async Task<GitSyncStatus> GetSyncStatusAsync(string repoPath)
public async Task FetchAsync(string repoPath)
public async Task KillAllSessionsAsync()
```

### Don't Block on Async
Never use `.Result` or `.Wait()` on async methods:

```csharp
// BAD - blocks thread, can deadlock WPF dispatcher
var result = GetStatusAsync(path).Result;

// GOOD - await properly
var result = await GetStatusAsync(path);
```

---

## 10. Logging

### Use FileLog
The codebase uses a custom `FileLog` static class for structured logging:

```csharp
using static CcDirector.Core.Utilities.FileLog;

// Log with component prefix
FileLog.Write($"[SessionManager] Created session {session.Id} for {repoPath}");
FileLog.Write($"[EmbeddedConsoleHost] Reattached to PID {pid}, hwnd=0x{hwnd:X}");
```

### Component Prefixes
Always include the component name in brackets:

```csharp
FileLog.Write($"[CcDirector] CC Director starting");
FileLog.Write($"[EmbeddedConsoleHost] AttachConsole failed: {error}");
FileLog.Write($"[EventRouter] Registered session {claudeSessionId}");
FileLog.Write($"[Reconnect] Adopted PID {pid} as session {id}");
```

### Include Context
Always log relevant IDs and values:

```csharp
// Good - includes context
FileLog.Write($"[SessionManager] Failed to kill session {session.Id}: {ex.Message}");

// Bad - no context
FileLog.Write("[SessionManager] Failed to kill session");
```

---

## 11. Comments and Documentation

### Code Comments
- Comments explain **why**, not **what**
- Code should be self-documenting through good names
- Update comments when code changes

```csharp
// Good - explains why
// Use case-insensitive comparison because session names come from user input
var session = sessions.FirstOrDefault(s =>
    string.Equals(s.CustomName, name, StringComparison.OrdinalIgnoreCase));

// Bad - explains what (obvious from code)
// Get the first session where name matches
var session = sessions.FirstOrDefault(s => s.CustomName == name);
```

### TODO Comments
Use TODO for known issues that will be addressed:

```csharp
// TODO: Add retry logic for transient pipe failures
// TODO: Cache git status to reduce process spawning
```

---

## 12. WPF-Specific Rules

### Rule 1: Async Void Event Handlers MUST Have Try-Catch

**Severity: BLOCKING - NON-NEGOTIABLE**

**Unhandled exceptions in `async void` methods WILL terminate the entire application.** There is no safety net. The process dies instantly with no chance to save state.

This is NOT a theoretical concern. This exact bug crashed CC Director repeatedly in production: a `DispatcherTimer.Tick` handler called an async method that threw `Win32Exception` when `git` was invoked with an invalid working directory. The unhandled exception killed the app every 30 seconds.

#### The Rule

Every `async void` method MUST wrap its entire body in try-catch:

```csharp
// GOOD - protected async void event handler
private async void SyncTimer_Tick(object? sender, EventArgs e)
{
    try
    {
        bool shouldFetch = (DateTime.UtcNow - _lastFetchTime).TotalSeconds >= 60;
        await RefreshSyncAsync(fetch: shouldFetch);
    }
    catch { /* prevent crash from async void */ }
}

// GOOD - protected button click handler
private async void BtnSendPrompt_Click(object sender, RoutedEventArgs e)
{
    try
    {
        await SendPromptAsync();
    }
    catch (Exception ex)
    {
        FileLog.Write($"[MainWindow] Send prompt failed: {ex.Message}");
    }
}

// BAD - unprotected, WILL crash the app on exception
private async void SyncTimer_Tick(object? sender, EventArgs e)
{
    await RefreshSyncAsync(fetch: true);  // If this throws, app dies
}
```

#### Where Async Void Is Acceptable

In WPF, `async void` is required for event handlers because the delegate signatures (`RoutedEventHandler`, `EventHandler`, etc.) return `void`. These are the ONLY acceptable uses:

- Button click handlers (`*_Click`)
- Timer tick handlers (`*_Tick`)
- Key down handlers (`*_KeyDown`)
- Other WPF event handlers

**Never use `async void` for any other method.** Use `async Task` instead.

### Rule 2: Validate Paths and External Inputs Before Process.Start

**Severity: BLOCKING**

Always validate that directories exist and paths are valid before passing them to `Process.Start`. Invalid paths cause `Win32Exception` which, in an async void handler, crashes the app.

```csharp
// GOOD - validate before use
private async Task RefreshSyncAsync(bool fetch = false)
{
    if (_repoPath is null || !Directory.Exists(_repoPath)) return;

    await _syncProvider.FetchAsync(_repoPath);
}

// BAD - passes unvalidated path
private async Task RefreshSyncAsync(bool fetch = false)
{
    if (_repoPath is null) return;

    await _syncProvider.FetchAsync(_repoPath);  // Crashes if _repoPath = "Unknown"
}
```

### Rule 3: Use Dispatcher.BeginInvoke for Cross-Thread UI Updates

**Severity: BLOCKING**

WPF controls can only be modified from the UI thread. Use `Dispatcher.BeginInvoke` for updates from background threads or async callbacks.

```csharp
// GOOD - safe from any thread
Application.Current?.Dispatcher.BeginInvoke(() =>
{
    session.Status = SessionStatus.Exited;
});

// GOOD - with priority for rendering operations
Dispatcher.BeginInvoke(
    System.Windows.Threading.DispatcherPriority.Render,
    UpdateConsolePosition);

// BAD - direct UI modification from background thread
Task.Run(() =>
{
    session.Status = SessionStatus.Exited;  // Cross-thread exception
});
```

### Rule 4: Freeze Brushes for Thread Safety

**Severity: WARNING**

WPF `Freezable` objects (brushes, geometries) must be frozen before use across threads. Use the `Freeze()` helper pattern:

```csharp
// GOOD - frozen static brushes
private static readonly SolidColorBrush BrushModified =
    Freeze(new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00)));

private static SolidColorBrush Freeze(SolidColorBrush brush)
{
    brush.Freeze();
    return brush;
}

// BAD - unfrozen brush used in static field
private static readonly SolidColorBrush BrushModified =
    new SolidColorBrush(Color.FromRgb(0xCC, 0xA7, 0x00));  // Not thread-safe
```

### Rule 5: DispatcherTimer for UI Polling

**Severity: WARNING**

Use `DispatcherTimer` (not `System.Timers.Timer` or `System.Threading.Timer`) for periodic UI updates. It fires on the UI thread, avoiding cross-thread issues.

```csharp
// GOOD - DispatcherTimer for UI polling
_pollTimer = new DispatcherTimer
{
    Interval = TimeSpan.FromSeconds(5)
};
_pollTimer.Tick += PollTimer_Tick;
_pollTimer.Start();

// Always stop and null timers on detach/dispose
public void Detach()
{
    _pollTimer?.Stop();
    _pollTimer = null;
}
```

### Rule 6: P/Invoke Conventions

**Severity: WARNING**

When using P/Invoke:
- Centralize Win32 declarations in a `NativeMethods` class where practical
- Use `SetLastError = true` for Win32 APIs that set last error
- Use `CharSet = CharSet.Unicode` for text APIs
- Use `SafeFileHandle` for kernel handles

```csharp
[DllImport("kernel32.dll", SetLastError = true)]
[return: MarshalAs(UnmanagedType.Bool)]
private static extern bool AttachConsole(uint dwProcessId);

[DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
```

---

## 13. Testing Considerations

### Design for Testability
- Return result objects with complete information
- Use constructor parameters for dependencies
- Keep methods focused and side-effect free when possible
- Use `InternalsVisibleTo` for testing internal members

```xml
<!-- In CcDirector.Core.csproj -->
<ItemGroup>
  <InternalsVisibleTo Include="CcDirector.Core.Tests" />
</ItemGroup>
```

### Test with cmd.exe
Since `claude.exe` may not be available in CI, tests use `cmd.exe` as a stand-in:

```csharp
var options = new AgentOptions { ClaudePath = "cmd.exe" };
var manager = new SessionManager(options);
```

### Accept Optional Paths for Testability
Methods that read/write specific files should accept optional path parameters:

```csharp
// Good - testable with custom path
public static async Task InstallAsync(string scriptPath, Action<string> log,
    string? settingsPath = null)
{
    settingsPath ??= DefaultSettingsPath;
    // ...
}
```

---

## 14. The Boundary Principle

Try-catch belongs at **boundaries** - the points where your code meets the outside world.

### WPF Boundaries (MUST have try-catch)

1. **Event handlers** - `async void` button clicks, timer ticks, key handlers
2. **Pipe message handlers** - Named pipe receive callbacks
3. **Process exit callbacks** - `Process.Exited` event handlers
4. **Dispose methods** - Cleanup must not crash

### Internal Code (Let exceptions bubble)

- Private helper methods
- Service/provider methods (use result objects for expected failures)
- Pure business logic

```
USER ACTION (click, timer tick, pipe message)
    |
    v
+-------------------+
| EVENT HANDLER     |  <-- TRY-CATCH HERE (boundary)
| BtnSend_Click()   |
+-------------------+
    |
    v
+-------------------+
| HELPER METHOD     |  <-- NO try-catch (let it bubble)
| SendPromptAsync() |
+-------------------+
    |
    v
+-------------------+
| PROVIDER          |  <-- Result object for expected failures
| RunGitAsync()     |     Throw for unexpected failures
+-------------------+
```

---

## Quick Reference

| Aspect | Convention | Example |
|--------|------------|---------|
| Warnings | Treat as errors | `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` |
| Classes | PascalCase | `SessionManager`, `GitStatusProvider` |
| Methods | PascalCase, Verb+Noun | `GetStatus()`, `CreateSession()` |
| Properties | PascalCase | `SessionId`, `IsActive` |
| Private fields | _camelCase | `_sessionManager`, `_pollTimer` |
| Parameters | camelCase | `repoPath`, `sessionId` |
| Async methods | Suffix with Async | `GetStatusAsync()`, `FetchAsync()` |
| Namespaces | File-scoped | `namespace CcDirector.Core.Sessions;` |
| Null checks | Pattern matching | `if (x is null)` |
| Validation | Check early, return/throw | `if (_repoPath is null) return;` |
| Errors | Log + result object | `FileLog.Write(...); return new Result { Success = false }` |
| Logging | FileLog with component prefix | `FileLog.Write($"[Component] message")` |
| Static | For stateless utilities | `HookRelayScript.EnsureWritten()` |
| Comments | Explain why, not what | `// Case-insensitive for user input` |
| Async void | MUST wrap in try-catch | `try { await ...; } catch { }` |
| Dispatcher | BeginInvoke for UI updates | `Dispatcher.BeginInvoke(() => ...)` |
| Brushes | Always freeze statics | `brush.Freeze()` |
| Timers | DispatcherTimer only | `new DispatcherTimer { Interval = ... }` |
| Paths | Validate before Process.Start | `if (!Directory.Exists(path)) return;` |
| Access | Internal for controlled creation | `internal Session(...)` |

---

## Summary

The CC Director codebase prioritizes:

1. **Reliability** - Async void handlers are protected, paths are validated, brushes are frozen
2. **Readability** - Code should be obvious to the next developer
3. **Maintainability** - Changes should be safe and localized
4. **Simplicity** - The simplest solution that works correctly

When in doubt, choose the simpler, more explicit approach.
