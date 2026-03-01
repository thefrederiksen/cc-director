# cc-comm-queue - Communication Queue CLI

Use this tool to submit content for human approval before posting.

## Location
`%LOCALAPPDATA%\cc-director\bin\cc-comm-queue.exe`

## Basic Commands

```bash
# LinkedIn post
cc-comm-queue add linkedin post "Content" --persona mindzie --tags "tag1,tag2"

# LinkedIn comment
cc-comm-queue add linkedin comment "Comment" --persona personal --context-url "URL"

# Email
cc-comm-queue add email email "Body" --email-to "to@email.com" --email-subject "Subject"

# Email with attachment
cc-comm-queue add email email "Body" --email-to "to@email.com" --email-subject "Subject" --email-attach "path/to/file.pdf"

# Reddit post
cc-comm-queue add reddit post "Content" --reddit-subreddit "r/sub" --reddit-title "Title"

# Check status
cc-comm-queue status
```

## Personas
- `mindzie` - CTO of mindzie (process mining content)
- `consulting` - Consulting services persona
- `personal` - Personal

## Full Documentation
- Agent instructions: See `docs/CommunicationManager/agent_instructions.md`
- CLI reference: See `docs/CC_TOOLS.md` (cc-comm-queue section)
