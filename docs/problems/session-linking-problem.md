# Problem: Claude Session Linking and Verification

## Overview

CC Director needs to reliably connect its internal sessions to Claude Code's session files. Currently the UI shows colored status indicators (orange, green, blue) but they are:
1. Cramped on a single line - hard to read
2. Missing clear status descriptions
3. No way to verify/inspect the linked session file

## Where Claude Code Sessions Are Stored

Claude Code stores session data in:

```
%USERPROFILE%\.claude\projects\{sanitized-repo-path}\
```

For example, `D:\ReposFred\cc_director` becomes:
```
C:\Users\soren\.claude\projects\D--ReposFred-cc-director\
```

### Files in Each Project Folder

| File | Purpose |
|------|---------|
| `sessions-index.json` | Index of all sessions with metadata (summary, firstPrompt, messageCount, dates) |
| `{session-id}.jsonl` | The actual conversation transcript (JSON lines format) |

### sessions-index.json Structure

```json
{
  "version": 1,
  "originalPath": "D:\\ReposFred\\cc_director",
  "entries": [
    {
      "sessionId": "abc123-def456-...",
      "fullPath": "C:\\Users\\soren\\.claude\\projects\\D--ReposFred-cc-director\\abc123-def456-....jsonl",
      "firstPrompt": "Fix the session persistence bug...",
      "summary": "Session persistence fix",
      "messageCount": 42,
      "created": "2026-02-15T10:30:00Z",
      "modified": "2026-02-16T04:00:00Z",
      "gitBranch": "main",
      "isSidechain": false
    }
  ]
}
```

### {session-id}.jsonl Structure

Each line is a JSON object representing a message:

```json
{"type":"user","message":"Fix the session persistence bug...","timestamp":"..."}
{"type":"assistant","message":"I'll help you fix that...","timestamp":"..."}
```

---

## Current Status Indicators

Looking at the UI screenshot, each session item shows colored boxes:

| Color | Current Meaning | What User Sees |
|-------|----------------|----------------|
| Orange | Unknown/needs clarification | Number (e.g., "15") |
| Green | Connected/OK | "OK" text |
| Blue | Unknown/needs clarification | Number (e.g., "9") |

### Problems with Current Display

1. **Too cramped** - All indicators on one line, hard to parse
2. **No labels** - User doesn't know what orange vs blue means
3. **No verification action** - Can't click to open/verify the .jsonl file
4. **No detailed status** - Just "OK" doesn't explain what was verified

---

## Proposed UI Improvements

### Session List Item Layout

Change from cramped single-line to expanded multi-line format:

```
+------------------------------------------+
| mindzieWeb - Data Designer               |
| Status: Working                          |
| Claude: 89d90716... [Verified] [Open]    |
| Messages: 15  |  Tokens: 2.3k            |
+------------------------------------------+
```

Each session item should show (on separate lines):
1. **Session Name** - custom name or repo folder
2. **Activity Status** - Idle, Working, WaitingForInput, etc.
3. **Claude Link Status** - with verification and action button
4. **Stats** - message count, token usage (if available)

### Claude Link Status Line

This line should show:

| Status | Display | Color | Action |
|--------|---------|-------|--------|
| Not Linked | `Claude: Not linked` | Gray | [Link] button |
| Verified | `Claude: abc123... [Verified]` | Green | [Open] button |
| File Not Found | `Claude: abc123... [Missing]` | Red | [Relink] button |
| Content Mismatch | `Claude: abc123... [Mismatch]` | Yellow | [Inspect] button |
| Error | `Claude: abc123... [Error]` | Red | [Details] button |

### Action Buttons

| Button | Action |
|--------|--------|
| [Open] | Opens the .jsonl file in default editor (or Explorer "..." menu -> Open) |
| [Link] | Opens dialog to manually link to a Claude session |
| [Relink] | Opens dialog to select a different Claude session |
| [Inspect] | Shows comparison of expected vs actual first prompt |
| [Details] | Shows the error message |

---

## Verification Logic

### Current Implementation

Located in `ClaudeSessionReader.cs`:

1. `VerifySessionFile()` - Checks if .jsonl exists and reads first prompt
2. `ReadFirstPromptFromJsonl()` - Extracts first user message from .jsonl
3. `NormalizeForComparison()` - Normalizes text for matching

### Verification Statuses (SessionVerificationStatus enum)

| Status | Meaning |
|--------|---------|
| `Verified` | .jsonl exists and first prompt matches expected |
| `FileNotFound` | No .jsonl file for the session ID |
| `NotLinked` | No ClaudeSessionId set on the session |
| `Error` | Exception while reading file |
| `ContentMismatch` | File exists but first prompt doesn't match |

### What Needs to Be Added

1. **UI display** - Show verification status clearly with colors/icons
2. **Open file action** - Ability to open .jsonl in editor/Explorer
3. **Relink action** - UI to manually change the linked Claude session
4. **Real-time updates** - Re-verify when Claude sends session events

---

## Code Locations

| Component | File | Purpose |
|-----------|------|---------|
| Session verification | `ClaudeSessionReader.cs` | Read/verify Claude session files |
| Session model | `Session.cs` | VerificationStatus, VerifiedFirstPrompt properties |
| Session management | `SessionManager.cs` | RegisterClaudeSession, RelinkClaudeSession |
| UI (session list) | `MainWindow.xaml` / `.xaml.cs` | Display session items |

---

## Implementation Tasks

### Phase 1: Expand Session Item UI

1. Change session list item template to multi-line layout
2. Add separate lines for: Name, Status, Claude Link, Stats
3. Increase item height (we only have ~6 sessions, space is not an issue)

### Phase 2: Claude Link Status Display

1. Show ClaudeSessionId (truncated) with verification status
2. Color-code by verification status (green=verified, red=missing, yellow=mismatch)
3. Add tooltip with full details

### Phase 3: Action Buttons

1. Add [Open] button to open .jsonl file
2. Add [Relink] button to change linked session
3. Add context menu with additional options

### Phase 4: Verification Tools

1. Add "Verify All Sessions" command
2. Show verification results in a summary dialog
3. Log verification results to FileLog

---

## Files to Modify

| File | Changes |
|------|---------|
| `MainWindow.xaml` | Session item template - expand to multi-line |
| `MainWindow.xaml.cs` | Add handlers for Open/Relink buttons |
| `Session.cs` | Already has VerificationStatus (may need events) |
| `ClaudeSessionReader.cs` | May need helper for opening files |

---

## Testing Scenarios

1. **Happy path** - Session linked, .jsonl exists, content matches
2. **Missing file** - ClaudeSessionId set but .jsonl deleted
3. **Wrong session** - ClaudeSessionId points to different conversation
4. **No link** - New session before first Claude response
5. **Relink** - User manually changes linked session
6. **Open file** - Verify file opens in correct location

---

## Questions to Resolve

1. What should the orange and blue numbers currently represent?
2. Should [Open] open the file in editor or show in Explorer?
3. Should we add a "Verify All" button to the toolbar?
4. What happens when Claude sends a new session ID during resume?
