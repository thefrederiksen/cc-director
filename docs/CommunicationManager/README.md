# Communication Manager

A visual review hub for content created by AI agents across multiple platforms. Review, edit, approve, or reject content before it gets posted.

## Overview

The Communication Manager is a WPF desktop application that serves as a central coordinator for content destined for LinkedIn, Twitter/X, Reddit, YouTube, Email, and Blog platforms. It does not generate content - it displays content created by other agents (like Claude Code) and lets you review and approve it.

## Key Features

- **Visual Review Queue**: List view with details panel for reviewing content
- **Multi-Platform Support**: LinkedIn, Twitter/X, Reddit, YouTube, Email, Blog
- **Multi-Persona Support**: mindzie (CTO), Consulting (Business), Personal brand
- **Content Actions**: Approve, Reject, Edit (inline), Skip, Delete
- **Context Links**: Open the original article/post being responded to in browser
- **Destination Links**: Open where the content will be posted
- **Auto-Refresh**: File watcher detects new content automatically
- **Status Tracking**: Pending, Approved, Rejected, Posted

## Directory Structure

```
tools/communication_manager/
  content/
    pending_review/     # New content awaiting review
    approved/           # Approved, ready for posting agent
    rejected/           # Rejected content
    posted/             # Successfully posted content
  docs/
    content_schema.md   # Full JSON schema documentation
  src/
    CommunicationManager/  # WPF application
```

## Workflow

1. **Agents create content**: Claude Code or other agents write JSON files to `content/pending_review/`
2. **You review**: Open Communication Manager, see all pending items
3. **Take action**: Approve (moves to approved/), Reject (moves to rejected/), Edit, or Delete
4. **Posting agent**: A separate agent picks up approved content and posts it
5. **Confirmation**: After posting, content moves to posted/ with the actual post URL

## JSON Content Format

See `docs/content_schema.md` for full documentation. Basic structure:

```json
{
  "id": "unique-guid",
  "platform": "linkedin | twitter | reddit | youtube | email | blog",
  "type": "post | comment | reply | message | article | email",
  "persona": "mindzie | center_consulting | personal",
  "persona_display": "CTO of mindzie",
  "content": "The actual text content",
  "created_by": "agent_name",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review",
  "context_url": "https://... (link to what we're responding to)",
  "destination_url": "https://... (where this will be posted)"
}
```

## Building

```bash
cd src/CommunicationManager
dotnet build
```

## Running

```bash
cd src/CommunicationManager
dotnet run
```

Or set the content path via environment variable:
```bash
set CC_COMM_CONTENT_PATH=C:\path\to\content
dotnet run
```

## Tech Stack

- C# / .NET 10
- WPF (Windows Presentation Foundation)
- CommunityToolkit.Mvvm for MVVM pattern

## Adding Content for Review

Any agent can drop JSON files into `content/pending_review/`. The file should:
1. Follow the schema in `docs/content_schema.md`
2. Have a `.json` extension
3. Include all required fields (id, platform, type, persona, content, etc.)

The application will automatically detect new files and display them for review.
