---
name: linkedin
version: 2026.03.05
description: Navigate LinkedIn web interface
site: linkedin.com
flavor: managed
forked_from: null
---

# LinkedIn Navigation Skill

## IMPORTANT: Tool Binding

LinkedIn has aggressive bot detection. NEVER use cc-browser directly for LinkedIn.
Use `cc-linkedin` for all LinkedIn operations -- it has human-like delays built in.
The only direct cc-browser use should be `cc-browser connections open linkedin`.

## Authentication Check

Logged-in state is indicated by:
- The global nav profile photo: `.global-nav__me-photo`
- The feed identity module: `div.feed-identity-module`

If the login button is visible (`a[data-tracking-control-name="guest_homepage-basic_nav-header-signin"]`),
the user is not authenticated.

## URL Patterns

| Page | URL |
|------|-----|
| Home feed | `https://www.linkedin.com/feed` |
| Profile | `https://www.linkedin.com/in/{username}` |
| Post | `https://www.linkedin.com/feed/update/urn:li:activity:{id}` |
| Search (all) | `https://www.linkedin.com/search/results/all/?keywords={query}` |
| Search (people) | `https://www.linkedin.com/search/results/people/?keywords={query}` |
| Search (companies) | `https://www.linkedin.com/search/results/companies/?keywords={query}` |
| Search (posts) | `https://www.linkedin.com/search/results/content/?keywords={query}` |
| Search (jobs) | `https://www.linkedin.com/search/results/jobs/?keywords={query}` |
| Messaging | `https://www.linkedin.com/messaging` |
| Message thread | `https://www.linkedin.com/messaging/thread/{thread_id}` |
| Connections | `https://www.linkedin.com/mynetwork/invite-connect/connections` |
| Invitations | `https://www.linkedin.com/mynetwork/invitation-manager` |
| My Network | `https://www.linkedin.com/mynetwork` |
| People You May Know | `https://www.linkedin.com/mynetwork/grow` |
| Notifications | `https://www.linkedin.com/notifications` |
| Jobs | `https://www.linkedin.com/jobs` |
| Company page | `https://www.linkedin.com/company/{company_id}` |

### Search Network Filters

For people search, append network degree filter:
- 1st degree: `&network=["F"]`
- 2nd degree: `&network=["S"]`
- 3rd degree: `&network=["O"]`

## Key Elements

LinkedIn uses React with data-testid attributes and ARIA labels.

### Navigation
| Element | Selector |
|---------|----------|
| Home | `a[href="https://www.linkedin.com/feed/"]` |
| My Network | `a[href="https://www.linkedin.com/mynetwork/"]` |
| Jobs | `a[href="https://www.linkedin.com/jobs/"]` |
| Messaging | `a[href="https://www.linkedin.com/messaging/"]` |
| Notifications | `a[href="https://www.linkedin.com/notifications/"]` |

### Feed / Posts
| Element | Selector |
|---------|----------|
| Post container | `div.feed-shared-update-v2` |
| Post text | `div.feed-shared-text` |
| Post author | `span.feed-shared-actor__name` |
| Author headline | `span.feed-shared-actor__description` |
| Post time | `span.feed-shared-actor__sub-description` |
| Reactions count | `button[aria-label*="reactions"]` |
| Comments count | `button[aria-label*="comment"]` |

### Post Actions
| Element | Selector |
|---------|----------|
| Like | `button[aria-label*="Like"]` |
| Comment | `button[aria-label*="Comment"]` |
| Repost | `button[aria-label*="Repost"]` |
| Send | `button[aria-label*="Send"]` |

### Comment Input
| Element | Selector |
|---------|----------|
| Comment editor | `div.ql-editor[data-placeholder="Add a comment"]` |
| Submit comment | `button.comments-comment-box__submit-button` |

### Profile
| Element | Selector |
|---------|----------|
| Name (h1) | `h1.text-heading-xlarge` |
| Headline | `div.text-body-medium` |
| Location | `span.text-body-small` |
| Connection count | `span.t-bold` |
| About section | `section.pv-about-section` |
| Search result card | `div.entity-result__item` |

### Connection Buttons
| Element | Selector |
|---------|----------|
| Connect | `button[aria-label*="Connect"]` |
| Message (connected) | `button[aria-label*="Message"]` |
| Pending | `button[aria-label*="Pending"]` |

### Messaging
| Element | Selector |
|---------|----------|
| Thread card | `div.msg-conversation-card` |
| Message input | `div.msg-form__contenteditable` |
| Send button | `button.msg-form__send-button` |
| Message body | `div.msg-s-event-listitem__body` |

### Search
| Element | Selector |
|---------|----------|
| Search input | `input[aria-label="Search"]` |
| Results container | `div.search-results-container` |
| Result item | `div.entity-result` |

### Invitations
| Element | Selector |
|---------|----------|
| Invitation card | `li.invitation-card` |
| Accept button | `button[aria-label*="Accept"]` |
| Ignore button | `button[aria-label*="Ignore"]` |

## Workflows

### View a Profile
1. Navigate to `https://www.linkedin.com/in/{username}`
2. Wait for `h1.text-heading-xlarge` to appear
3. Name is in h1, headline in `div.text-body-medium` below it

### Search for People
1. Navigate to search URL with `keywords={query}` and type `people`
2. Wait for `div.search-results-container`
3. Each result is a `div.entity-result` card

### Read Messages
1. Navigate to `https://www.linkedin.com/messaging`
2. Thread list: `div.msg-conversation-card` elements
3. Click a thread to open it
4. Messages are in `div.msg-s-event-listitem__body`

## Gotchas

- **Bot detection**: LinkedIn actively detects automation. NEVER use cc-browser directly.
  Always use cc-linkedin which has random delays and human-like behavior.
- **Rate limiting**: Too many page loads in succession triggers captchas.
- **Dynamic loading**: Feed content loads lazily on scroll. The initial snapshot
  may only show a few posts.
- **Selector instability**: LinkedIn updates their UI frequently. If selectors
  stop working, use `cc-browser snapshot --interactive` to discover current refs.
- **Login walls**: Many pages redirect to login if session expired.
  Check for the login button selector before proceeding.
