# cc-linkedin

A command-line tool for LinkedIn interactions via browser automation.

## Overview

cc-linkedin enables programmatic LinkedIn interactions through browser automation, acting exactly like a human user. No API keys required.

**Key Features:**
- Browse your feed and view posts
- Like and comment on posts
- View profiles and send connection requests
- Send and receive messages
- Search for people, posts, and companies

## Requirements

- cc-browser daemon running (port 9280)
- Chrome/Edge browser
- Logged into LinkedIn in the browser

## Quick Start

```bash
# Start cc-browser daemon with a LinkedIn workspace
cc-browser daemon --workspace linkedin

# Check LinkedIn login status
cc-linkedin status

# Show current user
cc-linkedin whoami

# View your feed
cc-linkedin feed --limit 10

# View a profile
cc-linkedin profile johndoe

# Send a connection request
cc-linkedin connect johndoe --note "Would love to connect!"

# Send a message
cc-linkedin message johndoe --text "Hello! Great to connect."

# Search for people
cc-linkedin search "software engineer" --type people
```

## Commands

### Status & Authentication
| Command | Description |
|---------|-------------|
| `status` | Check daemon and LinkedIn login status |
| `whoami` | Show current logged-in user |
| `me` | View your own profile summary |

### Feed & Content
| Command | Description |
|---------|-------------|
| `feed` | View home feed (--limit N) |
| `post URL` | View a specific post |
| `like URL` | Like a post |
| `comment URL --text` | Comment on a post |
| `repost URL` | Repost to your feed (--thoughts optional) |
| `save URL` | Save a post for later |

### Profiles & Networking
| Command | Description |
|---------|-------------|
| `profile USERNAME` | View someone's profile |
| `connections` | List your connections |
| `connect USERNAME` | Send connection request (--note optional) |
| `company SLUG` | View company page information |

### Invitations
| Command | Description |
|---------|-------------|
| `invitations` | View pending connection requests |
| `accept NAME` | Accept invitation from person |
| `ignore NAME` | Ignore invitation from person |

### Notifications
| Command | Description |
|---------|-------------|
| `notifications` | View notifications (--unread for unread only) |

### Messaging
| Command | Description |
|---------|-------------|
| `messages` | View recent messages (--unread for unread only) |
| `message USERNAME --text` | Send a message |

### Search & Jobs
| Command | Description |
|---------|-------------|
| `search QUERY --type` | Search (people/posts/companies/jobs) |
| `jobs QUERY` | Search for jobs (--location optional) |

### Navigation & Utilities
| Command | Description |
|---------|-------------|
| `goto URL` | Navigate to LinkedIn URL |
| `snapshot` | Get page snapshot for debugging |
| `screenshot` | Take screenshot |

## Global Options

```
--workspace TEXT  cc-browser workspace name or alias (default: from config.json)
--format TEXT     Output format: text, json, markdown
--delay FLOAT     Delay between actions (seconds)
--verbose         Detailed output for debugging
```

## Browser Setup

1. Start cc-browser daemon with a workspace for LinkedIn:
   ```bash
   cc-browser daemon --workspace linkedin
   ```

2. Log into LinkedIn in the browser window that opens

3. Your session persists - cc-linkedin uses the same browser session

## How It Works

```
cc-linkedin (Python CLI)
    |
    | HTTP requests to localhost:9280
    v
cc-browser daemon (Node.js)
    |
    | Chrome DevTools Protocol
    v
Chrome browser (logged into LinkedIn)
```

## Examples

### View Feed and Like Posts
```bash
# Get your feed
cc-linkedin feed --limit 5

# View a specific post
cc-linkedin post "https://www.linkedin.com/feed/update/urn:li:activity:..."

# Like it
cc-linkedin like "https://www.linkedin.com/feed/update/urn:li:activity:..."
```

### Networking
```bash
# Search for people in your industry
cc-linkedin search "product manager" --type people --limit 10

# View a profile
cc-linkedin profile janedoe

# Send connection request with note
cc-linkedin connect janedoe --note "Hi Jane, I saw your post about product strategy. Would love to connect!"
```

### Messaging
```bash
# View your messages
cc-linkedin messages

# Send a message to a connection
cc-linkedin message johndoe --text "Thanks for connecting! Looking forward to staying in touch."
```

## Part of cc-tools

This tool is part of the [cc-tools](https://github.com/sfrederico/cc-tools) suite.
