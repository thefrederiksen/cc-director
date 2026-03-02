# cc-reddit

A command-line tool for Reddit interactions via browser automation.

## Overview

cc-reddit enables programmatic Reddit interactions through browser automation, acting exactly like a human user. No API keys required.

**Key Features:**
- Browse subreddits and posts
- Create posts and comments
- Vote, save, and engage with content
- Manage subscriptions
- Send and receive messages
- Moderation tools (for moderators)

## Requirements

- cc-browser daemon running (workspace resolved via `~/.cc-reddit/config.json`)
- Chrome/Edge browser
- Logged into Reddit in the browser

## Quick Start

```bash
# First time: open browser UI to log into Reddit
cc-browser start --workspace reddit

# Start cc-browser daemon (for automation)
cc-browser daemon --workspace reddit

# Check Reddit login status
cc-reddit status

# Show current user
cc-reddit whoami

# View subreddit feed
cc-reddit feed programming --limit 10

# View a post
cc-reddit post "https://reddit.com/r/programming/comments/abc123/..."

# Create a post
cc-reddit post create learnpython --title "Question about loops" --body "How do I..."

# Comment on a post
cc-reddit comment abc123 --text "Great explanation!"

# Upvote
cc-reddit upvote abc123
```

## Commands

### Status & Authentication
| Command | Description |
|---------|-------------|
| `status` | Check if logged into Reddit |
| `whoami` | Show current username |

### Reading
| Command | Description |
|---------|-------------|
| `feed [SUBREDDIT]` | View subreddit feed |
| `post [URL]` | View post content |
| `comments [URL]` | View post comments |
| `search [QUERY]` | Search Reddit |
| `user [USERNAME]` | View user profile |
| `inbox` | View messages |
| `saved` | View saved items |

### Writing
| Command | Description |
|---------|-------------|
| `post create` | Create new post |
| `comment` | Comment on post |
| `reply` | Reply to comment |
| `edit` | Edit post/comment |
| `delete` | Delete post/comment |

### Engagement
| Command | Description |
|---------|-------------|
| `upvote` | Upvote post/comment |
| `downvote` | Downvote post/comment |
| `save` | Save post/comment |
| `join` | Join subreddit |
| `leave` | Leave subreddit |

### Messaging
| Command | Description |
|---------|-------------|
| `message` | Send direct message |

### Moderation
| Command | Description |
|---------|-------------|
| `mod approve` | Approve item |
| `mod remove` | Remove item |
| `mod ban` | Ban user |
| `mod queue` | View modqueue |

## Options

```
--workspace TEXT  Browser workspace (default from ~/.cc-reddit/config.json, or "reddit")
--format TEXT     Output: text, json, markdown
--delay FLOAT     Delay between actions (seconds)
--verbose         Detailed output
```

## Browser Setup

1. Start cc-browser to open the browser UI and log into Reddit:
   ```bash
   cc-browser start --workspace reddit
   ```

2. Log into Reddit in the browser window that opens

3. Start the daemon for automation:
   ```bash
   cc-browser daemon --workspace reddit
   ```

4. Your session persists - cc-reddit uses the same browser session

## How It Works

```
cc-reddit (Python CLI)
    |
    | HTTP requests to localhost:<daemonPort>
    v
cc-browser daemon (Node.js)
    |
    | Chrome DevTools Protocol
    v
Chrome browser (logged into Reddit)
```

## Part of cc-director

This tool is part of the [cc-director](https://github.com/sfrederico/cc-director) suite.
