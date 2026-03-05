---
name: reddit
version: 2026.03.05
description: Navigate Reddit web interface
site: reddit.com
flavor: managed
forked_from: null
---

# Reddit Navigation Skill

## IMPORTANT: Tool Binding

Reddit has aggressive bot detection. NEVER use cc-browser directly for Reddit.
Use `cc-reddit` for all Reddit operations -- it has human-like delays built in.
The only direct cc-browser use should be `cc-browser connections open reddit`.

## Authentication Check

Logged-in state on new Reddit (www.reddit.com):
- Account dropdown: `faceplate-dropdown-menu[name="account"]`
- Username display: `span[slot="trigger"]`

If `a[href*="login"]` is prominent, user is not authenticated.

## URL Patterns

| Page | URL |
|------|-----|
| Home | `https://www.reddit.com` |
| Subreddit | `https://www.reddit.com/r/{name}/{sort}` (sort: hot, new, top, rising) |
| Post | `https://www.reddit.com/r/{subreddit}/comments/{post_id}/{slug}` |
| Post by ID | `https://www.reddit.com/comments/{post_id}` |
| User profile | `https://www.reddit.com/user/{username}` |
| Submit post | `https://www.reddit.com/r/{subreddit}/submit` |
| Inbox | `https://www.reddit.com/message/inbox` |
| Unread | `https://www.reddit.com/message/unread` |
| Compose | `https://www.reddit.com/message/compose?to={username}` |
| Search | `https://www.reddit.com/search?q={query}` |
| Search in sub | `https://www.reddit.com/r/{subreddit}/search?q={query}` |
| Saved | `https://www.reddit.com/user/me/saved` |
| Mod queue | `https://www.reddit.com/r/{subreddit}/about/modqueue` |

## Key Elements (New Reddit - Shreddit Web Components)

Reddit's new interface uses custom web components (shreddit-*).

### Navigation
| Element | Selector |
|---------|----------|
| Main content | `main` |
| Subreddit feed | `shreddit-feed` |

### Posts
| Element | Selector |
|---------|----------|
| Post container | `shreddit-post` |
| Post title | `a[slot="title"]` |
| Post author | `a[data-testid="post_author_link"]` |
| Subreddit link | `a[data-testid="subreddit-name"]` |
| Score | `faceplate-number` |
| Comments link | `a[data-testid="comments-link"]` |
| Timestamp | `faceplate-timeago` |
| Post body text | `div[slot="text-body"]` |
| Post media | `shreddit-player, shreddit-gallery, img[src*="redd.it"]` |

### Comments
| Element | Selector |
|---------|----------|
| Comment tree | `shreddit-comment-tree` |
| Comment | `shreddit-comment` |
| Comment author | `a[data-testid="comment_author_link"]` |
| Comment body | `div[slot="comment"]` |
| Comment score | `span[data-testid="comment-upvote-count"]` |
| Reply button | `button[data-testid="comment-reply-button"]` |

### Voting
| Element | Selector |
|---------|----------|
| Upvote | `button[upvote]` |
| Downvote | `button[downvote]` |
| Upvoted state | `[upvoted="true"]` |
| Downvoted state | `[downvoted="true"]` |

### Actions
| Element | Selector |
|---------|----------|
| Save | `button[data-testid="save-button"]` |
| Share | `button[data-testid="share-button"]` |
| Hide | `button[data-testid="hide-button"]` |
| Report | `button[data-testid="report-button"]` |

### Post Creation
| Element | Selector |
|---------|----------|
| Create post button | `a[href*="/submit"]` |
| Text type | `button[aria-label="Text"]` |
| Link type | `button[aria-label="Link"]` |
| Image type | `button[aria-label="Image"]` |
| Title input | `textarea[name="title"]` |
| Body input | `div[contenteditable="true"]` |
| URL input | `input[name="url"]` |
| Submit button | `button[type="submit"]` |

### Comment Input
| Element | Selector |
|---------|----------|
| Comment composer | `div[data-testid="comment-composer"]` |
| Submit comment | `button[type="submit"]` |

### Subreddit
| Element | Selector |
|---------|----------|
| Join button | `button[data-testid="join-button"]` |
| Leave button | `button[data-testid="leave-button"]` |
| Sidebar | `shreddit-subreddit-sidebar` |
| Rules | `div[data-testid="rules-content"]` |

### User Profile
| Element | Selector |
|---------|----------|
| Karma | `span[data-testid="karma"]` |
| Posts tab | `a[href*="/submitted"]` |
| Comments tab | `a[href*="/comments"]` |

### Inbox
| Element | Selector |
|---------|----------|
| Unread link | `a[href="/message/unread"]` |
| Inbox link | `a[href="/message/inbox"]` |
| Message item | `div[data-testid="message"]` |

### Search
| Element | Selector |
|---------|----------|
| Search input | `input[type="search"]` |
| Search results | `div[data-testid="search-results"]` |

### Moderation
| Element | Selector |
|---------|----------|
| Mod tools | `button[aria-label="Moderator tools"]` |
| Approve | `button[data-testid="approve-button"]` |
| Remove | `button[data-testid="remove-button"]` |
| Spam | `button[data-testid="spam-button"]` |
| Lock | `button[data-testid="lock-button"]` |
| Distinguish | `button[data-testid="distinguish-button"]` |
| Sticky | `button[data-testid="sticky-button"]` |

## Old Reddit Selectors (old.reddit.com)

For users on old Reddit, key differences:
- Posts: `div.thing.link` with `a.title`, `a.author`, `a.comments`
- Comments: `div.comment` with `div.md` for body
- Voting: `div.arrow.up` / `div.arrow.down`
- Login form: `form#login-form`
- Search: `input[name="q"]`

## Workflows

### Read a Post
1. Navigate to post URL
2. Wait for `shreddit-post` element
3. Title in `a[slot="title"]`, body in `div[slot="text-body"]`
4. Comments in `shreddit-comment` elements within `shreddit-comment-tree`

### Browse a Subreddit
1. Navigate to `https://www.reddit.com/r/{name}`
2. Wait for `shreddit-feed`
3. Posts are `shreddit-post` elements

### Search
1. Navigate to search URL with `q={query}`
2. Wait for `div[data-testid="search-results"]`

## Gotchas

- **Bot detection**: Reddit detects automation. NEVER use cc-browser directly.
  Always use cc-reddit which has random delays and human-like typing.
- **Shreddit components**: New Reddit uses custom web components. Standard CSS
  selectors may not pierce shadow DOM. Use `cc-browser snapshot --interactive`
  to see the actual DOM structure.
- **Infinite scroll**: Content loads on scroll. Initial snapshot shows limited posts.
- **Login walls**: Some content requires authentication. Check for login prompts.
- **Old vs New Reddit**: Default is new Reddit. Some users prefer old.reddit.com
  which has completely different selectors.
