# cc-reddit - Product Requirements Document

## Executive Summary

cc-reddit is a command-line tool for automating Reddit interactions through browser automation, enabling users to perform all Reddit actions as if they were a real user. Part of the cc-tools suite.

## The Problem

Users who want to automate Reddit interactions face several challenges:

1. **API Limitations**: Reddit's API has strict rate limits, requires OAuth setup, and restricts certain actions
2. **Detection**: API-based automation is easily detected and flagged
3. **Incomplete Access**: Many Reddit features are not exposed via API (awards, chat, certain mod actions)
4. **Account Risk**: Using third-party Reddit clients or automation libraries risks account suspension

## The Solution

cc-reddit uses browser automation (via cc-browser) to interact with Reddit exactly like a human user would:

- No API keys required
- Full feature access (everything visible in the browser is accessible)
- Natural interaction patterns that mirror real user behavior
- Session persistence with Reddit's own authentication

## Target Audience

### Primary
- Power users who want to automate repetitive Reddit tasks
- Researchers collecting Reddit data for analysis
- Moderators managing multiple subreddits
- Content creators scheduling and managing posts

### Secondary
- Developers building Reddit integrations
- Social media managers handling Reddit presence
- Archivists preserving Reddit content

## Architecture

```
Claude Code / User
    |
    v
cc-reddit CLI (Python + Typer)
    | HTTP (localhost:9280)
    v
cc-browser Daemon
    | CDP (localhost:9222)
    v
Chrome (logged into Reddit)
```

### Design Principles

1. **Browser-First**: All actions go through the browser, never direct API calls
2. **Human-Like**: Actions include natural delays and patterns
3. **Session-Based**: Leverages Reddit's browser session/cookies
4. **Composable**: Each command does one thing well, can be chained

## Functional Requirements

### Authentication & Session

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-AUTH-1 | Check if logged into Reddit | P0 |
| FR-AUTH-2 | Support multiple Reddit accounts via browser workspaces | P0 |
| FR-AUTH-3 | Show current logged-in username | P0 |

### Reading Content

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-READ-1 | View subreddit feed (hot, new, top, rising) | P0 |
| FR-READ-2 | View post with all comments | P0 |
| FR-READ-3 | View user profile and posts/comments | P0 |
| FR-READ-4 | Search Reddit (posts, comments, subreddits, users) | P0 |
| FR-READ-5 | View inbox (messages, notifications) | P1 |
| FR-READ-6 | View saved posts/comments | P1 |
| FR-READ-7 | View post/comment with specific ID/URL | P0 |

### Writing Content

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-WRITE-1 | Create text post | P0 |
| FR-WRITE-2 | Create link post | P0 |
| FR-WRITE-3 | Create image/video post | P1 |
| FR-WRITE-4 | Comment on a post | P0 |
| FR-WRITE-5 | Reply to a comment | P0 |
| FR-WRITE-6 | Edit own post/comment | P1 |
| FR-WRITE-7 | Delete own post/comment | P1 |
| FR-WRITE-8 | Crosspost to another subreddit | P2 |

### Voting & Engagement

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-VOTE-1 | Upvote post/comment | P0 |
| FR-VOTE-2 | Downvote post/comment | P0 |
| FR-VOTE-3 | Save post/comment | P1 |
| FR-VOTE-4 | Hide post | P2 |
| FR-VOTE-5 | Report post/comment | P2 |
| FR-VOTE-6 | Give award | P2 |

### Messaging

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-MSG-1 | Send direct message to user | P1 |
| FR-MSG-2 | Reply to message | P1 |
| FR-MSG-3 | List inbox messages | P1 |
| FR-MSG-4 | Mark message as read | P2 |

### Subreddit Management

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-SUB-1 | Join/leave subreddit | P0 |
| FR-SUB-2 | List joined subreddits | P1 |
| FR-SUB-3 | Get subreddit info/rules | P1 |

### Moderation (for moderators)

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-MOD-1 | Approve/remove post or comment | P2 |
| FR-MOD-2 | Ban/unban user | P2 |
| FR-MOD-3 | View modqueue | P2 |
| FR-MOD-4 | Distinguish/sticky comment | P2 |
| FR-MOD-5 | Lock/unlock post | P2 |

### Data Export

| ID | Requirement | Priority |
|----|-------------|----------|
| FR-EXPORT-1 | Export post content as JSON/markdown | P1 |
| FR-EXPORT-2 | Export comments as JSON/markdown | P1 |
| FR-EXPORT-3 | Export user's post/comment history | P2 |

## Non-Functional Requirements

| ID | Requirement |
|----|-------------|
| NFR-1 | All commands complete within 30 seconds (network dependent) |
| NFR-2 | Graceful error handling with clear messages |
| NFR-3 | No Reddit API keys required |
| NFR-4 | Works with standard Chrome/Edge browser |
| NFR-5 | Human-like delays between actions (configurable) |
| NFR-6 | Support for old.reddit.com and new.reddit.com |
| NFR-7 | Output in plain text, JSON, or markdown format |

## Technical Decisions

### Language: Python
- Consistent with other cc-tools (cc-gmail, cc-markdown)
- Rich CLI support via Typer
- Easy HTTP client (requests/httpx) for cc-browser communication

### Dependencies
- `typer` - CLI framework
- `rich` - Terminal output formatting
- `httpx` - HTTP client for cc-browser communication
- `pydantic` - Data validation and models

### Browser Automation
- Uses cc-browser daemon (already built)
- Communicates via HTTP API on localhost:9280
- No direct Playwright dependency (cc-browser handles it)

### Build
- PyInstaller for standalone executable
- Same pattern as cc-gmail, cc-markdown

## CLI Interface Design

### Global Options
```
--workspace TEXT    Browser workspace to use (default: "reddit")
--format TEXT     Output format: text, json, markdown (default: text)
--delay FLOAT     Seconds to wait between actions (default: 1.0)
--verbose         Show detailed output
```

### Commands

#### Authentication
```bash
# Check login status
cc-reddit status

# Show current username
cc-reddit whoami
```

#### Reading
```bash
# View subreddit feed
cc-reddit feed [SUBREDDIT] [--sort hot|new|top|rising] [--limit 25]

# View specific post
cc-reddit post [POST_URL_OR_ID]

# View comments on a post
cc-reddit comments [POST_URL_OR_ID] [--sort best|top|new|controversial]

# Search
cc-reddit search [QUERY] [--subreddit SUBREDDIT] [--type posts|comments|subreddits|users]

# View user profile
cc-reddit user [USERNAME] [--posts] [--comments]

# View inbox
cc-reddit inbox [--unread]

# View saved items
cc-reddit saved
```

#### Writing
```bash
# Create text post
cc-reddit post create [SUBREDDIT] --title "Title" --body "Body text"

# Create link post
cc-reddit post create [SUBREDDIT] --title "Title" --url "https://..."

# Create image post
cc-reddit post create [SUBREDDIT] --title "Title" --image /path/to/image.png

# Comment on post
cc-reddit comment [POST_URL_OR_ID] --text "Your comment"

# Reply to comment
cc-reddit reply [COMMENT_URL_OR_ID] --text "Your reply"

# Edit post/comment
cc-reddit edit [URL_OR_ID] --text "Updated text"

# Delete post/comment
cc-reddit delete [URL_OR_ID]
```

#### Voting
```bash
# Upvote
cc-reddit upvote [URL_OR_ID]

# Downvote
cc-reddit downvote [URL_OR_ID]

# Save
cc-reddit save [URL_OR_ID]

# Unsave
cc-reddit unsave [URL_OR_ID]
```

#### Subreddits
```bash
# Join subreddit
cc-reddit join [SUBREDDIT]

# Leave subreddit
cc-reddit leave [SUBREDDIT]

# List joined subreddits
cc-reddit subscriptions

# Get subreddit info
cc-reddit subreddit [NAME] [--rules]
```

#### Messaging
```bash
# Send message
cc-reddit message [USERNAME] --subject "Subject" --body "Message body"

# Reply to message
cc-reddit message reply [MESSAGE_ID] --body "Reply text"
```

#### Moderation
```bash
# Approve item
cc-reddit mod approve [URL_OR_ID]

# Remove item
cc-reddit mod remove [URL_OR_ID] [--reason "Reason"]

# Ban user
cc-reddit mod ban [SUBREDDIT] [USERNAME] [--days 30] [--reason "Reason"]

# View modqueue
cc-reddit mod queue [SUBREDDIT]
```

## Implementation Phases

### Phase 1: Foundation (MVP)
- [x] Directory structure
- [ ] CLI scaffolding with Typer
- [ ] cc-browser HTTP client wrapper
- [ ] Reddit page detection and navigation
- [ ] `status` command (check login)
- [ ] `whoami` command
- [ ] `feed` command (view subreddit)
- [ ] `post` command (view post)

### Phase 2: Reading & Navigation
- [ ] `comments` command
- [ ] `search` command
- [ ] `user` command
- [ ] `subreddit` command
- [ ] JSON/markdown output formats

### Phase 3: Writing & Engagement
- [ ] `post create` command
- [ ] `comment` command
- [ ] `reply` command
- [ ] `upvote` / `downvote` commands
- [ ] `join` / `leave` commands

### Phase 4: Advanced Features
- [ ] `inbox` command
- [ ] `message` command
- [ ] `saved` command
- [ ] `edit` / `delete` commands

### Phase 5: Moderation & Polish
- [ ] Moderation commands
- [ ] Error handling improvements
- [ ] Rate limiting / anti-detection
- [ ] Documentation
- [ ] PyInstaller build

## Reddit Page Selectors (Research Notes)

### Key URLs
- Home: `https://www.reddit.com/`
- Subreddit: `https://www.reddit.com/r/{subreddit}/`
- Post: `https://www.reddit.com/r/{subreddit}/comments/{post_id}/{slug}/`
- User: `https://www.reddit.com/user/{username}/`
- Inbox: `https://www.reddit.com/message/inbox/`
- Submit: `https://www.reddit.com/r/{subreddit}/submit`

### Element Patterns (New Reddit)
These will need validation during implementation:
- Login button: `button[data-testid="login-button"]`
- Post title input: `textarea[name="title"]`
- Post body input: Rich text editor (contenteditable)
- Comment input: `div[data-testid="comment-composer"]`
- Upvote button: `button[data-testid="upvote-button"]`
- Downvote button: `button[data-testid="downvote-button"]`
- Join button: `button[data-testid="join-button"]`

### Element Patterns (Old Reddit - old.reddit.com)
- More predictable HTML structure
- Form-based submissions
- May be more automation-friendly

## Out of Scope for V1

1. **Reddit Chat** - Different WebSocket-based system
2. **Reddit Live** - Real-time threads
3. **Reddit Polls** - Complex voting interface
4. **Multi-account switching** - Use browser profiles instead
5. **Scheduled posting** - Use external scheduler
6. **Analytics/metrics** - Out of scope
7. **Mobile-specific features** - Desktop browser only

## Risk Mitigation

| Risk | Mitigation |
|------|------------|
| Reddit UI changes break selectors | Use data-testid attributes where possible; fall back to text/role matching |
| Rate limiting | Configurable delays; human-like patterns |
| Account suspension | Clear documentation on responsible use |
| CAPTCHAs | Pause and prompt user to solve manually |
| Two-factor auth | Support via browser profile with saved session |
| New vs Old Reddit | Support both with separate selector sets |

## Success Criteria

- [ ] All P0 requirements implemented and tested
- [ ] Can create a post, comment, and vote without errors
- [ ] Works with both new.reddit.com and old.reddit.com
- [ ] Clear error messages for common failure modes
- [ ] Documentation with usage examples
- [ ] PyInstaller executable builds successfully

## File Structure

```
src/cc-reddit/
├── src/
│   ├── __init__.py
│   ├── __main__.py           # Entry point
│   ├── cli.py                # Typer CLI commands
│   ├── browser_client.py     # cc-browser HTTP client wrapper
│   ├── reddit.py             # Reddit-specific page interactions
│   ├── selectors.py          # CSS selectors for Reddit elements
│   ├── models.py             # Pydantic models (Post, Comment, User, etc.)
│   └── formatters.py         # Output formatting (text, json, markdown)
│
├── tests/
│   ├── __init__.py
│   ├── test_cli.py
│   └── test_reddit.py
│
├── requirements.txt
├── pyproject.toml
├── cc-reddit.spec
├── build.ps1
├── README.md
└── PRD.md                    # This document
```

## Next Steps

1. Create the file structure above
2. Implement browser_client.py (HTTP wrapper for cc-browser)
3. Implement basic CLI with `status` and `whoami` commands
4. Add `feed` and `post` viewing commands
5. Iterate through implementation phases
