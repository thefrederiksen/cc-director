# cc-comm-queue

CLI tool for adding content to the Communication Manager approval queue.

## Installation

```bash
# Build and deploy executable
cd tools/cc-comm-queue
.\release.bat
```

## Usage

### Add Content

```bash
# LinkedIn post
cc-comm-queue add linkedin post "Process mining trends for 2024..." \
    --persona mindzie \
    --tags "process mining,trends"

# LinkedIn comment
cc-comm-queue add linkedin comment "Great insights!" \
    --persona personal \
    --context-url "https://linkedin.com/posts/..."

# Email
cc-comm-queue add email email "Hi Sarah, following up..." \
    --persona mindzie \
    --email-to "sarah@techcorp.com" \
    --email-subject "Following up from Summit"

# Reddit post
cc-comm-queue add reddit post "How we reduced processing time..." \
    --persona personal \
    --reddit-subreddit "r/processimprovement" \
    --reddit-title "Case Study: 70% reduction"
```

### JSON Input

```bash
# From file
cc-comm-queue add-json content.json

# From stdin
cat content.json | cc-comm-queue add-json -
```

### Queue Management

```bash
# List pending items
cc-comm-queue list --status pending

# Show queue stats
cc-comm-queue status

# Show specific item
cc-comm-queue show abc123
```

### Configuration

```bash
# Show config
cc-comm-queue config show

# Set queue path
cc-comm-queue config set queue_path "D:/path/to/content"

# Set default persona
cc-comm-queue config set default_persona mindzie
```

## JSON Output (for Agents)

Use `--json` flag for machine-readable output:

```bash
cc-comm-queue add linkedin post "Hello" --json
# {"success": true, "id": "abc123...", "file": "path/to/file.json"}
```

## Supported Platforms

- `linkedin` - LinkedIn posts, comments, messages
- `twitter` - Tweets, replies, threads
- `reddit` - Posts and comments
- `youtube` - Comments
- `email` - Emails via cc-outlook
- `blog` - Long-form articles

## Personas

- `mindzie` - CTO of mindzie
- `consulting` - Consulting services persona
- `personal` - Personal
