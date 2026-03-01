# cc_comm_queue - Communication Queue CLI

Use this tool to submit content for human approval before posting.

## Location
`C:\cc-tools\cc_comm_queue.exe`

## Basic Commands

```bash
# LinkedIn post
cc_comm_queue add linkedin post "Content" --persona mindzie --tags "tag1,tag2"

# LinkedIn comment
cc_comm_queue add linkedin comment "Comment" --persona personal --context-url "URL"

# Email
cc_comm_queue add email email "Body" --email-to "to@email.com" --email-subject "Subject"

# Reddit post
cc_comm_queue add reddit post "Content" --reddit-subreddit "r/sub" --reddit-title "Title"

# Check status
cc_comm_queue status
```

## Personas
- `mindzie` - CTO of mindzie (process mining content)
- `consulting` - Consulting services persona
- `personal` - Personal

## Full Documentation
- Agent instructions: See `docs/CommunicationManager/agent_instructions.md`
- CLI reference: See `docs/CC_TOOLS.md` (cc_comm_queue section)
