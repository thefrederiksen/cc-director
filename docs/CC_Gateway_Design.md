# CC Gateway Design Document

**Status:** Approved for Implementation
**Date:** 2026-02-18
**Author:** Architecture Planning Session

---

## Overview

CC Gateway is a Windows service that provides remote access to Claude Code sessions managed by CC Director. It enables users to interact with their personal AI assistant through Discord while away from their desktop.

---

## Architectural Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| Deployment | **Separate Windows Service** | Cleaner architecture, can run headless, independent lifecycle |
| Chat Platform | **Discord only** (initially) | Already have Discord integration via voicebridge; add others later |
| Document Delivery | **Discord upload** | Files posted directly as attachments; simple and immediate |
| Response Format | **Wait + Clean output** | Wait for completion, extract relevant output, remove ANSI codes |

---

## System Architecture

```
Discord Bot  -->  CC Gateway (Windows Service)  -->  CC Director
     ^                   |                              |
     |                   v                              v
     +-- Clean text -- REST API  <--  Sessions  <--  Claude Code
                         |
                         v
                    File Attachments
                    (Discord Upload)
```

### Components

1. **CC Gateway Service** (`cc_gateway`)
   - Windows Service hosting ASP.NET Core
   - REST API on `http://localhost:5555/api/`
   - Discord bot client
   - Output cleaning/formatting

2. **CC Director** (existing, modified)
   - Exposes session control methods
   - Provides terminal output access
   - Runs as desktop application

---

## REST API Design

### Base URL
```
http://localhost:5555/api/
```

### Endpoints

#### Sessions

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/sessions` | List all active sessions |
| `GET` | `/sessions/{id}` | Get session details |
| `POST` | `/sessions` | Create new session |
| `DELETE` | `/sessions/{id}` | Close session |
| `GET` | `/sessions/{id}/status` | Get session status (Running, WaitingForInput, etc.) |

#### Interaction

| Method | Endpoint | Description |
|--------|----------|-------------|
| `POST` | `/sessions/{id}/send` | Send text input to session |
| `GET` | `/sessions/{id}/output` | Get recent output (last N lines) |
| `GET` | `/sessions/{id}/output/since/{timestamp}` | Get output since timestamp |
| `POST` | `/sessions/{id}/interrupt` | Send Ctrl+C to session |

#### Files

| Method | Endpoint | Description |
|--------|----------|-------------|
| `GET` | `/sessions/{id}/files` | List files in session's working directory |
| `GET` | `/sessions/{id}/files/{path}` | Download specific file |

### Request/Response Examples

**Create Session:**
```json
POST /api/sessions
{
  "repoPath": "D:\\ReposFred\\my-project",
  "name": "my-project"
}

Response:
{
  "id": "abc123",
  "name": "my-project",
  "repoPath": "D:\\ReposFred\\my-project",
  "status": "Starting"
}
```

**Send Input:**
```json
POST /api/sessions/abc123/send
{
  "text": "Fix the bug in auth.js"
}

Response:
{
  "accepted": true,
  "timestamp": "2026-02-18T10:30:00Z"
}
```

**Get Output:**
```json
GET /api/sessions/abc123/output?lines=50

Response:
{
  "sessionId": "abc123",
  "status": "WaitingForInput",
  "lines": [
    { "timestamp": "...", "text": "I'll fix the bug in auth.js..." },
    { "timestamp": "...", "text": "Reading file..." }
  ],
  "isComplete": true
}
```

---

## Discord Bot Design

### Commands

| Command | Description |
|---------|-------------|
| `/ask <message>` | Send message to active session |
| `/sessions` | List all sessions |
| `/switch <name>` | Switch active session |
| `/status` | Show current session status |
| `/new <repo>` | Create new session for repository |
| `/close` | Close current session |

### Response Handling

1. **Wait for Completion**
   - Monitor session status until `WaitingForInput`
   - Timeout after configurable period (default: 5 minutes)

2. **Clean Output**
   - Remove ANSI escape codes
   - Remove terminal control sequences
   - Preserve meaningful formatting (code blocks, etc.)

3. **Chunking**
   - Discord max message: 2000 characters
   - Split long responses into multiple messages
   - Use code blocks for terminal output

4. **File Attachments**
   - Detect when Claude creates/modifies files
   - Offer to upload as Discord attachment
   - Auto-upload for explicitly requested documents

### User Session Mapping

```
Discord User ID  -->  Active Session ID
     |
     v
Per-user state stored in Gateway
```

---

## Output Cleaning

### ANSI Code Removal

Remove these sequences from terminal output:
- Color codes: `\x1b[31m`, `\x1b[0m`, etc.
- Cursor movement: `\x1b[2J`, `\x1b[H`, etc.
- Terminal control: `\x1b[?25h`, `\x1b[?25l`, etc.

### Content Extraction

1. Identify Claude's response boundaries
2. Extract the meaningful content
3. Preserve code blocks and formatting
4. Remove redundant status lines

### Example Transformation

**Raw Terminal:**
```
[?25l[2K[1G> Reading file auth.js...
[?25h[32m[+][0m Found the issue on line 42
[32m[+][0m Fixing null check...
```

**Cleaned Output:**
```
Reading file auth.js...
[+] Found the issue on line 42
[+] Fixing null check...
```

---

## Director Integration

### Required Modifications to CC Director

#### SessionManager.cs

Add methods for remote access:

```csharp
// Get session by ID
public Session? GetSessionById(string sessionId);

// Get all sessions with status
public IEnumerable<SessionInfo> GetAllSessions();

// Create session programmatically
public Session CreateSession(string repoPath, string? name = null);

// Close session
public void CloseSession(string sessionId);
```

#### Session.cs

Add methods for output access:

```csharp
// Get recent output lines
public IEnumerable<OutputLine> GetRecentOutput(int lineCount);

// Get output since timestamp
public IEnumerable<OutputLine> GetOutputSince(DateTime since);

// Get current status
public SessionStatus GetStatus(); // Running, WaitingForInput, etc.

// Send input programmatically
public void SendInput(string text);
```

### IPC Mechanism

**Option A: Named Pipes**
- Fast, Windows-native
- Director hosts pipe server
- Gateway connects as client

**Option B: Local HTTP**
- Director hosts small HTTP endpoint
- Gateway calls via HttpClient
- Simpler to implement

**Recommended: Named Pipes** for performance and reliability.

---

## Project Structure

```
src/
  CcDirector.Gateway/
    Program.cs                 # Windows service entry point
    GatewayService.cs          # Main service implementation

    Api/
      Controllers/
        SessionsController.cs  # REST API for sessions
        FilesController.cs     # REST API for files
      Models/
        SessionDto.cs
        OutputDto.cs

    Discord/
      DiscordBotClient.cs      # Discord.NET integration
      CommandHandler.cs        # Slash command handling
      ResponseFormatter.cs     # Output cleaning and formatting

    Director/
      DirectorClient.cs        # IPC client to Director
      IDirectorClient.cs       # Interface for testing

    Config/
      GatewayConfig.cs         # Configuration model
      appsettings.json         # Default configuration
```

---

## Configuration

### appsettings.json

```json
{
  "Gateway": {
    "ApiPort": 5555,
    "ApiHost": "localhost"
  },
  "Discord": {
    "Token": "YOUR_BOT_TOKEN",
    "AllowedUsers": ["123456789"],
    "DefaultTimeout": 300
  },
  "Director": {
    "PipeName": "cc_director_gateway",
    "ConnectionTimeout": 5000
  },
  "Output": {
    "MaxLines": 100,
    "CleanAnsiCodes": true
  }
}
```

---

## Security Considerations

1. **Local-Only API**
   - REST API binds to localhost only
   - Not exposed to network

2. **Discord Authentication**
   - AllowedUsers whitelist
   - Only specified Discord user IDs can interact

3. **No Secrets in Output**
   - Filter environment variables from output
   - Redact file paths containing sensitive info

---

## Implementation Phases

### Phase 1: Local REST Gateway
- [ ] Create cc_gateway project (Windows Service)
- [ ] Implement REST API endpoints
- [ ] Add Director IPC client (named pipes)
- [ ] Modify Director to host pipe server
- [ ] Test with HTTP client (curl/Postman)

### Phase 2: Discord Integration
- [ ] Add Discord.NET package
- [ ] Implement bot connection
- [ ] Create slash commands
- [ ] Implement response formatting
- [ ] Add file upload capability

### Phase 3: Output Intelligence
- [ ] ANSI code stripping
- [ ] Session status detection
- [ ] Response boundary detection
- [ ] Smart chunking for long responses

### Phase 4: Polish
- [ ] Error handling and recovery
- [ ] Logging and diagnostics
- [ ] Configuration validation
- [ ] Documentation

---

## Dependencies

### NuGet Packages

| Package | Purpose |
|---------|---------|
| `Microsoft.Extensions.Hosting.WindowsServices` | Windows Service hosting |
| `Microsoft.AspNetCore.OpenApi` | REST API |
| `Discord.Net` | Discord bot client |
| `System.IO.Pipelines` | Named pipe IPC |

---

## Testing Strategy

1. **Unit Tests**
   - Output cleaning logic
   - Response formatting
   - Command parsing

2. **Integration Tests**
   - REST API endpoints
   - Director IPC communication

3. **Manual Testing**
   - Discord bot interaction
   - End-to-end session control

---

## Open Items for Future

- Slack adapter
- Teams adapter
- Web UI for remote terminal viewing
- Mobile app integration
- Multi-user support
