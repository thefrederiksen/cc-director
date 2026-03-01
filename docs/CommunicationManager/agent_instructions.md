# Agent Instructions: Communication Manager Queue

This document provides instructions for AI agents that need to submit content for human approval before posting to social media or sending communications.

## Overview

The **cc_comm_queue** CLI tool allows agents to add content to the Communication Manager approval queue. A human reviewer will approve, edit, or reject the content before it is posted.

## Tool Location

- **Executable:** `C:\cc-tools\cc_comm_queue.exe`
- **Documentation:** `C:\cc-tools\CC_TOOLS.md` (search for "cc_comm_queue" section)

## Quick Reference

```bash
# Add a LinkedIn post
cc_comm_queue add linkedin post "Your post content here" --persona mindzie --tags "tag1,tag2"

# Add a LinkedIn comment
cc_comm_queue add linkedin comment "Your comment" --context-url "https://linkedin.com/posts/..."

# Add an email
cc_comm_queue add email email "Email body" --email-to "recipient@example.com" --email-subject "Subject line"

# Add a Reddit post
cc_comm_queue add reddit post "Post content" --reddit-subreddit "r/subreddit" --reddit-title "Post Title"

# Check queue status
cc_comm_queue status
```

## Supported Platforms

| Platform | Types | Notes |
|----------|-------|-------|
| linkedin | post, comment, message | Use --linkedin-visibility for public/connections |
| twitter | post, comment, reply | For threads, add multiple items |
| reddit | post, comment | Requires --reddit-subreddit |
| youtube | comment | For commenting on videos |
| email | email | Requires --email-to and --email-subject |
| blog | article | For long-form content |

## Personas

Always specify the correct persona:

| Persona | Display Name | Use For |
|---------|--------------|---------|
| `mindzie` | CTO of mindzie | Process mining, mindzie product content |
| `consulting` | Consulting services persona | Consulting services content |
| `personal` | Personal | Personal opinions, general content |

## Required Options by Platform

### LinkedIn Post
```bash
cc_comm_queue add linkedin post "Content" --persona mindzie
```

### LinkedIn Comment (responding to something)
```bash
cc_comm_queue add linkedin comment "Comment text" \
    --persona personal \
    --context-url "https://linkedin.com/posts/..." \
    --context-title "Title of post we're responding to"
```

### Email
```bash
cc_comm_queue add email email "Email body text" \
    --persona mindzie \
    --email-to "recipient@example.com" \
    --email-subject "Subject line"
```

### Reddit Post
```bash
cc_comm_queue add reddit post "Post content" \
    --persona personal \
    --reddit-subreddit "r/processimprovement" \
    --reddit-title "Post title goes here"
```

## JSON Output for Scripting

Use `--json` flag to get machine-readable output:

```bash
cc_comm_queue add linkedin post "Hello world" --json
```

Output:
```json
{"success": true, "id": "abc123...", "file": "path/to/file.json"}
```

## What Happens After Submission

1. Content is saved to `pending_review/` folder
2. Human reviewer sees it in the Communication Manager app
3. Reviewer can:
   - **Approve** - Content moves to `approved/` (ready for posting)
   - **Reject** - Content moves to `rejected/` with reason
   - **Edit** - Modify content before approving
4. Future: Posting agent will pick up approved content and post it

## Best Practices

1. **Always use appropriate persona** - Match the voice to the platform and content
2. **Include context URLs** - When responding to something, link to the original
3. **Add meaningful tags** - Helps with filtering and organization
4. **Keep content appropriate length** - LinkedIn posts ~1300 chars, tweets ~280 chars
5. **Add reviewer notes** - Use `--notes` to explain context for the human reviewer

## Example: Full LinkedIn Post

```bash
cc_comm_queue add linkedin post "Process mining has evolved significantly in 2024. Here are 3 trends I'm seeing:

1. AI-powered process discovery
2. Real-time process monitoring
3. Integration with automation platforms

What trends are you seeing in your organization?

#ProcessMining #AI #DigitalTransformation" \
    --persona mindzie \
    --tags "process mining,trends,thought leadership" \
    --notes "Part of Q1 thought leadership campaign"
```
