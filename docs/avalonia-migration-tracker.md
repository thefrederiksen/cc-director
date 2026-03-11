# Avalonia Migration Tracker

Status legend: DONE | PARTIAL | NOT STARTED | SKIPPED (intentionally omitted)

Last updated: 2026-03-11

---

## 1. Main Window Structure

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| 3-column layout (sidebar, splitter, content) | Yes | Yes | DONE |
| Grid splitter (resizable) | Yes | Yes | DONE |
| Window icon | Yes | Yes | DONE |
| Min size constraints | Yes | Yes | DONE |

## 2. Left Sidebar

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Hamburger menu button | Yes | Yes | DONE |
| "SESSIONS" header | Yes | Yes | DONE |
| "+ New Session" button | Yes | Yes | DONE |
| Session list with activity border color | Yes | Yes | DONE |
| Session display name | Yes | Yes | DONE |
| Activity label (idle/working/etc.) | Yes | Yes | DONE |
| Build info footer | Yes | Yes | DONE |
| Git branch indicator on session | Yes | No | NOT STARTED |
| Three-dot context menu per session | Yes | No | NOT STARTED |
| Session rename (via context menu) | Yes | No | NOT STARTED |
| Session color indicator | Yes | No | NOT STARTED |
| Open in Explorer (context menu) | Yes | No | NOT STARTED |
| Open in VS Code (context menu) | Yes | No | NOT STARTED |
| Open .jsonl in Explorer | Yes | No | NOT STARTED |
| Relink Session | Yes | No | NOT STARTED |
| Close Session (context menu) | Yes | No | NOT STARTED |
| Drag-reorder sessions | Yes | No | NOT STARTED |
| Documents sidebar panel | Yes | No | SKIPPED |
| Connections sidebar panel | Yes | No | SKIPPED |
| Writer sidebar panel | Yes | No | SKIPPED |
| Quick Actions sidebar panel | Yes | No | SKIPPED |
| Communications sidebar panel | Yes | No | SKIPPED |
| Claude Config gear button | Yes | No | NOT STARTED |

## 3. Application Menu (Hamburger)

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Save Workspace... | Yes | Yes | DONE |
| Load Workspace... | Yes | Yes | DONE |
| Clear Workspace | Yes | Yes | DONE |
| Open Logs | Yes | Yes | DONE |
| Repositories... | Yes | No | NOT STARTED |
| Accounts... | Yes | No | NOT STARTED |
| Open Sessions (file) | Yes | No | NOT STARTED |
| Open History (folder) | Yes | No | NOT STARTED |
| History in VS Code | Yes | No | NOT STARTED |

## 4. Top App Bar

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Claude View button | Yes | No | NOT STARTED |
| MCP button | Yes | No | NOT STARTED |
| Agents button | Yes | No | NOT STARTED |
| Settings button | Yes | No | NOT STARTED |
| Help button | Yes | No | NOT STARTED |

## 5. Session Header Banner

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Session name display | Yes | Yes | DONE |
| Activity state label | Yes | Yes | DONE |
| Blue background banner | Yes | Yes | DONE |
| Message count badge | Yes | No | NOT STARTED |
| Session ID display | Yes | No | NOT STARTED |
| Verification badge | Yes | No | NOT STARTED |
| Re-link button | Yes | No | NOT STARTED |
| Director ID display | Yes | No | NOT STARTED |
| Refresh Terminal button | Yes | No | NOT STARTED |
| Coaching icon/subtitle | Yes | No | SKIPPED |

## 6. Left Tab Bar (Main Content Tabs)

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Agent tab button | Yes | Yes | DONE |
| Terminal tab button | Yes | Yes | DONE |
| Source Control tab button | Yes | Yes | DONE |
| Tab switching (show/hide panels) | Yes | Yes | DONE |
| Active tab highlight | Yes | Yes | DONE |
| Terminal selected by default | Yes | Yes | DONE |

## 7. Agent Tab (Clean View)

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Text widget (Claude responses) | Yes | Yes | DONE |
| Thinking widget (collapsible) | Yes | Yes | DONE |
| Bash widget (command + output) | Yes | Yes | DONE |
| File widget (Read/Write/Edit) | Yes | Yes (merged into Tool) | DONE |
| Search widget (Grep/Glob) | Yes | Yes (merged into Tool) | DONE |
| Todo widget | Yes | Yes (merged into Tool) | DONE |
| Skill widget | Yes | Yes (merged into Tool) | DONE |
| Agent widget | Yes | Yes (merged into Tool) | DONE |
| Generic tool widget | Yes | Yes (merged into Tool) | DONE |
| User message widget (blue bubble) | Yes | Yes | DONE |
| Widget template selector | Yes | Yes (IDataTemplate) | DONE |
| Filter ComboBox (All/My/Conversation) | Yes | Yes | DONE |
| Progress bar (working state) | Yes | Yes | DONE |
| "Your Turn" indicator | Yes | Yes | DONE |
| Empty state text | Yes | Yes | DONE |
| Loading state text | Yes | Yes | DONE |
| Auto-scroll to bottom | Yes | Yes | DONE |
| JSONL file polling (2s) | Yes | Yes | DONE |
| StudioBackend live stream | Yes | Yes | DONE |
| Inject user prompt (immediate) | Yes | Yes | DONE |
| Markdown rendering (FlowDocument) | Yes | No (plain text only) | PARTIAL |
| Rewind button on user messages | Yes | No | NOT STARTED |
| Custom Expander template (chevron) | Yes | No (uses default) | PARTIAL |

## 8. Terminal Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| ConPTY terminal emulator | Yes | Yes | DONE |
| Placeholder text (no session) | Yes | Yes | DONE |
| Attach/Detach session | Yes | Yes | DONE |
| Summary panel (right side) | Yes | No | NOT STARTED |
| Workflow recording screenshots | Yes | No | NOT STARTED |

## 9. Source Control Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Branch name display | Yes | Yes | DONE |
| Staged changes section | Yes | Yes | DONE |
| Unstaged changes section | Yes | Yes | DONE |
| File tree view (hierarchical) | Yes | Yes | DONE |
| Status indicators (M/A/D/R/?) | Yes | Yes | DONE |
| Color-coded status | Yes | Yes | DONE |
| Auto-refresh polling | Yes | Yes | DONE |
| View File (context menu) | Yes | Yes | DONE |
| Copy Path (context menu) | Yes | Yes | DONE |
| Copy Relative Path | Yes | Yes | DONE |
| Add to .gitignore | Yes | Yes | DONE |
| Ahead/Behind/Behind-main badges | Yes | Yes | DONE |
| No upstream indicator | Yes | Yes | DONE |
| Integrated file viewer panel | Yes | No | NOT STARTED |

## 10. Prompt Bar

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Multi-line text input | Yes | Yes | DONE |
| Watermark text | Yes | Yes | DONE |
| Auto-height (expand with content) | Yes | Yes | DONE |
| Enter = Send | Yes | Yes | DONE |
| Shift+Enter = New line | Yes | Yes | DONE |
| Send button | Yes | Yes | DONE |
| Visible only when session active | Yes | Yes | DONE |
| Inject into CleanView on send | Yes | Yes | DONE |
| Slash command autocomplete | Yes | Yes | DONE |
| Voice input button (mic) | Yes | No | NOT STARTED |
| Queue prompt button | Yes | Yes | DONE |
| Intercept slash commands (native dialogs) | Yes | No | NOT STARTED |
| Notification bar (above prompt) | Yes | Yes | DONE |
| Handover button | Yes | Yes | DONE |
| Ctrl+Shift+Enter = Queue | Yes | Yes | DONE |
| Monospace font for input | Yes | Yes | DONE |
| Queue button badge (red when items) | Yes | Yes | DONE |
| Drag & drop file paths into prompt | Yes | No | NOT STARTED |

## 11. Right Panel

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Collapsible toggle button | Yes | Yes | DONE |
| 280px width | Yes | Yes | DONE |
| TabControl with tabs | Yes | Yes | DONE |

### 11a. Screenshots Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Screenshot thumbnails | Yes | Yes | DONE |
| Auto-discover directory | Yes | Yes | DONE |
| File watcher (auto-refresh) | Yes | Yes | DONE |
| Refresh button | Yes | Yes | DONE |
| Clear all button | Yes | Yes | DONE |
| View button (open in viewer) | Yes | Yes | DONE |
| Copy path button | Yes | Yes | DONE |
| Delete button | Yes | Yes | DONE |
| Time label | Yes | Yes | DONE |

### 11b. Queue Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Queue item list | Yes | Yes | DONE |
| Item count text | Yes | Yes | DONE |
| Pop button (insert to prompt) | Yes | Yes | DONE |
| Remove button | Yes | Yes | DONE |
| Clear button | Yes | Yes | DONE |
| Empty state text | Yes | Yes | DONE |
| Tab badge (count) | Yes | Yes | DONE |
| Move Up/Down reorder | Yes | No | NOT STARTED |
| Double-click to execute | Yes | No | NOT STARTED |

### 11c. Sessions Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Session browser control | Yes | No (placeholder) | NOT STARTED |
| Historical sessions grouped by project | Yes | No | NOT STARTED |
| Search/filter | Yes | No | NOT STARTED |
| Resume session from browser | Yes | No | NOT STARTED |

### 11d. Usage Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Usage dashboard control | Yes | Yes | DONE |
| Per-account utilization bars | Yes | Yes | DONE |
| Reset countdown | Yes | Yes | DONE |
| Refresh button | Yes | Yes | DONE |

### 11e. Hooks Tab

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Hook event list | Yes | Yes | DONE |
| Timestamp + event name | Yes | Yes | DONE |
| Session ID short | Yes | Yes | DONE |
| Detail line | Yes | Yes | DONE |
| Color-coded events | Yes | Yes | DONE |
| Clear button | Yes | Yes | DONE |
| Empty state | Yes | Yes | DONE |
| Filter by active session | Yes | Yes | DONE |
| Cap at 500 events | Yes | Yes | DONE |
| Auto-scroll to bottom | Yes | Yes | DONE |

## 12. Dialogs & Windows

| Dialog | WPF | Avalonia | Status |
|--------|-----|---------|--------|
| NewSessionDialog (3 tabs) | Yes | Yes | PARTIAL |
| -- New Session tab | Yes | Yes | DONE |
| -- Resume Session tab | Yes | Yes | DONE |
| -- Handovers tab | Yes | Yes | DONE |
| -- Sortable columns | Yes | Yes | DONE |
| -- Quick-launch cards (Assistant/Coach) | Yes | No | NOT STARTED |
| -- Bypass/Remote checkboxes | Yes | No | NOT STARTED |
| SaveWorkspaceDialog | Yes | Yes | DONE |
| LoadWorkspaceDialog | Yes | Yes | DONE |
| RenameSessionDialog | Yes | No | NOT STARTED |
| ResumeDialog | Yes | No | NOT STARTED |
| RelinkSessionDialog | Yes | No | NOT STARTED |
| RepositoryManagerDialog | Yes | No | NOT STARTED |
| AccountsDialog | Yes | No | NOT STARTED |
| RootDirectoryDialog | Yes | No | NOT STARTED |
| AgentTemplatesDialog | Yes | No | NOT STARTED |
| McpServersDialog | Yes | No | NOT STARTED |
| ClaudeViewDialog | Yes | No | NOT STARTED |
| ClaudeConfigDialog | Yes | No | NOT STARTED |
| SettingsView | Yes | No | NOT STARTED |
| StatsDialog | Yes | No | NOT STARTED |
| StatusDialog | Yes | No | NOT STARTED |
| MemoryDialog | Yes | No | NOT STARTED |
| HelpDialog | Yes | No | NOT STARTED |
| ThemeDialog | Yes | No | NOT STARTED |
| OutputStyleDialog | Yes | No | NOT STARTED |
| CloseDialog (exit confirmation) | Yes | No | NOT STARTED |
| SplashScreen | Yes | No | NOT STARTED |
| WorkspaceProgressDialog | Yes | No | NOT STARTED |
| GitHubRepoPickerDialog | Yes | No | NOT STARTED |
| GitHubIssuesDialog | Yes | No | NOT STARTED |
| CloneRepoDialog | Yes | No | NOT STARTED |
| AddConnectionDialog | Yes | No | NOT STARTED |
| WindowsTerminalWarningDialog | Yes | No | SKIPPED |

## 13. Workflow Automation

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| WorkflowEditorWindow | Yes | No | NOT STARTED |
| WorkflowRecorderWindow | Yes | No | NOT STARTED |
| WorkflowConditionDialog | Yes | No | NOT STARTED |
| WorkflowConfirmDialog | Yes | No | NOT STARTED |
| WorkflowParametersDialog | Yes | No | NOT STARTED |
| WorkflowParameterizeDialog | Yes | No | NOT STARTED |
| WorkflowVariableNameDialog | Yes | No | NOT STARTED |
| WorkflowRunsDialog | Yes | No | NOT STARTED |

## 14. Sidebar Feature Panels (WPF-only)

These are sidebar-replacing panels in WPF that haven't been planned for Avalonia yet:

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Quick Actions (ChatGPT-style) | Yes | No | SKIPPED |
| Communications Manager | Yes | No | SKIPPED |
| Document Library | Yes | No | SKIPPED |
| Connections Browser | Yes | No | SKIPPED |
| Content Writer | Yes | No | SKIPPED |

## 15. Voice Input

| Feature | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| Mic button on prompt bar | Yes | No | NOT STARTED |
| Vosk speech-to-text | Yes | No | NOT STARTED |
| TextInputDialog | Yes | No | NOT STARTED |

## 16. User Controls

| Control | WPF | Avalonia | Status |
|---------|-----|---------|--------|
| CleanView | Yes | Yes | DONE |
| GitChangesView/Control | Yes | Yes | DONE |
| UsageDashboardView | Yes | Yes | DONE |
| SessionBrowserView | Yes | No | NOT STARTED |
| SkillsConfigView | Yes | No | NOT STARTED |
| ConnectionsView | Yes | No | NOT STARTED |
| SimpleChatView | Yes | No | SKIPPED |
| QuickActionsView | Yes | No | SKIPPED |
| SettingsView | Yes | No | NOT STARTED |
| CodeViewerControl | Yes | No | NOT STARTED |
| MarkdownViewerControl | Yes | No | NOT STARTED |
| ImageViewerControl | Yes | No | NOT STARTED |
| PdfViewerControl | Yes | No | NOT STARTED |
| TextViewerControl | Yes | No | NOT STARTED |
| InputDialog | Yes | No | NOT STARTED |

---

## Summary

| Category | Total | Done | Partial | Not Started | Skipped |
|----------|-------|------|---------|-------------|---------|
| Main Window Structure | 4 | 4 | 0 | 0 | 0 |
| Left Sidebar | 23 | 7 | 0 | 11 | 5 |
| App Menu | 9 | 4 | 0 | 5 | 0 |
| Top App Bar | 5 | 0 | 0 | 5 | 0 |
| Session Header Banner | 10 | 3 | 0 | 6 | 1 |
| Left Tab Bar | 6 | 6 | 0 | 0 | 0 |
| Agent Tab (CleanView) | 23 | 20 | 2 | 1 | 0 |
| Terminal Tab | 5 | 3 | 0 | 2 | 0 |
| Source Control Tab | 14 | 13 | 0 | 1 | 0 |
| Prompt Bar | 17 | 14 | 0 | 3 | 0 |
| Right Panel Structure | 3 | 3 | 0 | 0 | 0 |
| Screenshots Tab | 10 | 10 | 0 | 0 | 0 |
| Queue Tab | 7 | 5 | 0 | 2 | 0 |
| Sessions Tab | 4 | 0 | 0 | 4 | 0 |
| Usage Tab | 4 | 4 | 0 | 0 | 0 |
| Hooks Tab | 11 | 11 | 0 | 0 | 0 |
| Dialogs | 33 | 7 | 1 | 24 | 1 |
| Workflow Automation | 8 | 0 | 0 | 8 | 0 |
| Sidebar Feature Panels | 5 | 0 | 0 | 0 | 5 |
| Voice Input | 3 | 0 | 0 | 3 | 0 |
| User Controls | 15 | 3 | 0 | 10 | 2 |
| **TOTAL** | **219** | **117** | **3** | **85** | **14** |

**Progress: 117/205 actionable items done (57%)**

---

## Priority Queue (Next Items to Work On)

### High Priority (core UX parity)
1. Prompt bar: slash command autocomplete
2. Prompt bar: queue prompt button
3. Session context menu (rename, close, open in Explorer/VS Code)
4. Session rename dialog (with color picker)
5. Session header: message count, session ID display

### Medium Priority (important features)
6. App menu: Repositories, Accounts
7. Sessions tab (SessionBrowserView)
8. NewSessionDialog: quick-launch cards, bypass/remote checkboxes
9. CleanView: rewind button on user messages
10. CleanView: custom Expander styling

### Lower Priority (nice to have)
11. Top app bar buttons (Claude View, MCP, Agents, Settings, Help)
12. Source control: integrated file viewer panel
13. Terminal: summary panel
14. Close dialog (exit confirmation)
15. Settings dialog
