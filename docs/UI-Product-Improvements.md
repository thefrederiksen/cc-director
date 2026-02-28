# CC Director - UI & Product Improvement Plan

## Context

The CC Director UI has grown organically and needs a deliberate redesign to clarify panel responsibilities, improve the header, and add new capabilities (markdown viewer/editor, settings page, Claude Code tool integration for settings). This plan defines the high-level blocks, terminology, and a phased improvement roadmap.

---

## Current State (As-Is)

```
+-----------------------------------------------------------------------+
| [=] SESSIONS  |  HeaderBanner (session name, usage, state badge)      |
|               |  -- messy: mixes app-global + session-specific info   |
+---------------+-------------------------------------------------------+
|               |                                    |>>| Queue  Hooks  |
| + New Session | Terminal | Source Control          |  |               |
|               |                                    |  |               |
| [session 1]   |  (ConPTY embedded terminal)        |  | hook events   |
| [session 2]   |                                    |  | or queue items|
| [session 3]   |                                    |  |               |
| [session 4]   |                                    |  |               |
|               |                                    |  |               |
|               +------------------------------------+  |               |
|               | [Prompt input box]     [Send][Queue]  |               |
|               |                                    |  |               |
+---------------+------------------------------------+--+---------------+
| Usage badges  |                                                       |
| Build: xxx    |                                                       |
+---------------+-------------------------------------------------------+
```

### Current Problems

1. **Header is a mess** - Mixes application-global info (usage limits) with session-specific info (session name, state badge, Claude ID)
2. **Source Control tab** shows even when there's no git repo
3. **Right panel tabs** still labeled "Queue" and "Hooks" (was "Pipes") - naming inconsistency
4. **No markdown viewer** - Must open VS Code for any file viewing
5. **No settings page** - Settings are scattered/missing
6. **No Claude Code tool integration** for changing settings
7. **Session list sidebar** doesn't extend to the top of the screen
8. **Terminology** is inconsistent across the app

---

## Proposed Layout (To-Be)

```
+-------------------------------------------------------------------------+
|  [=]  |                     APP BAR                                     |
|       |  Usage: [Pro 45%] [Team 12%]  |  [Settings]  [?]               |
| CC    |  (always visible, all accounts)                                 |
| DIR   +-----------------------------------+--+--------------------------+
| ECTOR |  SESSION BAR                      |  |                          |
|       |  "my-feature" | State: Working    |  |                          |
| +---+ +----------------------------------+  |                          |
| |New| | Terminal | Files | Source Control |  | Inspector                |
| +---+ |                                  |  |                          |
|       | +------------------------------+ |>>| [Queue] [Hooks]          |
| sess1 | |                              | |  |                          |
| sess2 | |   ConPTY Terminal Window      | |  | (hook events or          |
| sess3 | |   (unchanged, untouchable)    | |  |  queued prompts)         |
| sess4 | |                              | |  |                          |
| sess5 | |                              | |  |                          |
|       | +------------------------------+ |  |                          |
|       +----------------------------------+  +--------------------------+
|       | Notification bar                 |                              |
|       +----------------------------------+                              |
|       | [Prompt input ...        ] [Send][Queue][Refresh]               |
+-------+-----------------------------------------------------------------+
  sidebar          center workspace              inspector panel
```

---

## Block Definitions & Terminology

### 1. SIDEBAR (left, fixed)
- **Purpose**: Session navigation, app launching pad
- **Extends**: Full height of window (top to bottom)
- **Contains**:
  - App Menu button (hamburger)
  - App title/logo area
  - "+ New Session" button
  - Session List (scrollable)
  - Usage summary (compact, per-account) at bottom
  - Build info at very bottom
- **Behavior**: Always visible, never changes based on selection

### 2. APP BAR (top, right of sidebar)
- **Purpose**: Application-global information that is NOT session-specific
- **Contains**:
  - Usage limit bars/badges for ALL Claude accounts (always updating)
  - Settings button (opens Settings dialog)
  - Help/About button
- **Behavior**: Always visible whether or not a session is selected
- **Key change**: Separates app-global info from session-specific info

### 3. SESSION BAR (below App Bar, right of sidebar)
- **Purpose**: Session-specific header info
- **Contains**:
  - Session name (travels with selected session)
  - Activity state badge (Working, Your Turn, Needs Permission, etc.)
  - Message count badge
  - Claude Session ID + Verification
  - Refresh button
- **Behavior**: Only visible when a session is selected. Changes when you switch sessions.
- **Key change**: This was previously mixed into the single header banner

### 4. CENTER WORKSPACE (main area)
- **Purpose**: The primary working area, tabbed
- **Tabs**:
  - **Terminal** (default) - The ConPTY terminal. Untouchable, functions like a CLI.
  - **Files** (NEW) - Markdown viewer/editor, and later other file types
  - **Source Control** - Git changes view. Only shown when session directory has .git
- **Contains below tabs**:
  - Notification bar (status messages)
  - Prompt Bar (text input + Send/Queue/Refresh buttons)

### 5. INSPECTOR PANEL (right, collapsible)
- **Purpose**: Supplementary session info
- **Tabs**:
  - **Queue** - Prompt queue management
  - **Hooks** - Hook event log
- **Toggle**: The ">>" button to show/hide
- **Behavior**: Session-specific, collapses when hidden

### 6. PROMPT BAR (bottom of center workspace)
- **Purpose**: Helper input for talking to Claude Code
- **Contains**: Text input, Send button, Queue button, Refresh button
- **Behavior**: Only visible when a session is active

---

## Feature Roadmap

### Phase 1: Header Restructure (HIGH PRIORITY)
Split the current monolithic `SessionHeaderBanner` into two distinct areas:

**1a. App Bar** - Application-global strip
- Move usage badges here (they update for all accounts regardless of session)
- Add Settings gear icon button
- Always visible

**1b. Session Bar** - Session-specific strip
- Keep: session name, state badge, message count, Claude ID, verification
- Only visible when a session is selected
- Background uses session's custom color

**Files to modify:**
- `src/CcDirector.Wpf/MainWindow.xaml` - Restructure grid rows
- `src/CcDirector.Wpf/MainWindow.xaml.cs` - Update header population logic

### Phase 2: Sidebar Extension
- Extend sidebar column to span full window height (row 0 + row 1)
- Move app menu and branding to top of sidebar
- Clean up spacing and visual hierarchy

**Files to modify:**
- `src/CcDirector.Wpf/MainWindow.xaml` - Change grid row spans

### Phase 3: Source Control Conditional Display
- Only show "Source Control" tab when the session's working directory contains a `.git` folder
- Check on session selection and on file system changes
- Hide tab gracefully when not applicable

**Files to modify:**
- `src/CcDirector.Wpf/MainWindow.xaml.cs` - Add git detection logic
- `src/CcDirector.Wpf/MainWindow.xaml` - Bind tab visibility

### Phase 4: Files Tab (NEW FEATURE)
Add a "Files" tab to the center workspace for viewing/editing files:

**4a. Markdown Viewer**
- Raw mode: syntax-highlighted plain text
- Rendered mode: formatted markdown display
- Toggle between raw and rendered

**4b. Markdown Editor**
- Edit markdown files directly in CC Director
- Save back to disk
- No need to open VS Code for simple edits

**Technology: WebView2 + AvalonEdit**
- WebView2 for rendered markdown preview (Chromium-based, ships with Win11)
- AvalonEdit for raw/edit mode (syntax highlighting, proper text editing)
- Toggle between raw edit and rendered preview

**Files to create/modify:**
- New: `src/CcDirector.Wpf/Controls/MarkdownViewerControl.xaml`
- New: `src/CcDirector.Wpf/Controls/MarkdownEditorControl.xaml`
- `src/CcDirector.Wpf/MainWindow.xaml` - Add Files tab
- NuGet: `Microsoft.Web.WebView2`, `AvalonEdit`

### Phase 5: Settings Page (NEW FEATURE)
A modal dialog (popup) that consolidates ALL application configuration:

**Design principles:**
- Every setting has a clear description explaining WHY it exists
- Organized into logical sections with left-side navigation
- Simple, clean screens - not overwhelming
- Searchable
- **Consolidates existing dialogs** (Accounts, Repository Manager) as sections

**Sections:**
- General (theme, startup behavior, window preferences)
- Accounts (absorbs existing AccountsDialog functionality)
- Repositories (absorbs existing RepositoryManagerDialog functionality)
- Terminal (ConPTY settings, scrollback, font)
- Hooks (hook configuration)
- Engine (vault location, engine settings, scheduled tasks)
- Advanced (log level, debug options)

**Migration:** Hamburger menu items for "Repositories..." and "Accounts..." will open
the Settings dialog navigated to the relevant section. Existing dialog code can be
refactored into UserControls that live inside the Settings dialog.

**Files to create/modify:**
- New: `src/CcDirector.Wpf/Dialogs/SettingsDialog.xaml`
- New: `src/CcDirector.Engine/Settings/` - Settings model classes
- Refactor: `AccountsDialog` -> `Controls/AccountsSettingsPanel.xaml`
- Refactor: `RepositoryManagerDialog` -> `Controls/RepositoriesSettingsPanel.xaml`

### Phase 6: Claude Code Tool Integration for Settings (NEW FEATURE)
Expose CC Director settings as Claude Code tools so users can change settings via the terminal without clicking through UI.

**Examples:**
- "Change my theme to light mode"
- "Set scrollback to 10000 lines"
- "Show me where my vault is configured"
- "List all scheduled tasks"

**Implementation:**
- MCP server or hook-based tool registration
- Each setting exposed as a readable/writable tool
- Claude Code can query and modify settings programmatically

---

## Terminology Reference

| Term | Definition |
|------|-----------|
| **Sidebar** | Left panel - session list, navigation, always fixed |
| **App Bar** | Top strip - app-global info (usage, settings) |
| **Session Bar** | Below App Bar - session-specific info (name, state) |
| **Center Workspace** | Main tabbed area (Terminal, Files, Source Control) |
| **Terminal** | The ConPTY CLI window - untouchable, runs Claude Code |
| **Files** | New tab for viewing/editing files (markdown first) |
| **Source Control** | Git changes tab (conditional on .git presence) |
| **Inspector** | Right collapsible panel (Queue + Hooks tabs) |
| **Queue** | Prompt queue for batching messages to Claude |
| **Hooks** | Event log for Claude Code hook events |
| **Prompt Bar** | Bottom input area for sending text to Claude Code |
| **Settings** | Popup dialog for all application configuration |

---

## Priority Order

1. **Phase 1 + 2**: Header restructure + Sidebar extension (biggest visual mess, quick win)
2. **Phase 3**: Source Control conditional display (small fix, improves clarity)
3. **Phase 5**: Settings page (needed before tool integration)
4. **Phase 6**: Claude Code tool integration for settings (key differentiator)
5. **Phase 4**: Files tab / Markdown viewer-editor (new feature, more complex)

---

## What We Do NOT Change

- The terminal window itself (ConPTY) - it runs Claude Code and must function as a standard CLI
- The fundamental dark theme / VS Code aesthetic
- The session model and how sessions work
- Existing dialog functionality (New Session, Accounts, Repos, etc.)
