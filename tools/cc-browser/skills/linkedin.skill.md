---
name: linkedin
version: 2026.03.05.1
description: Navigate LinkedIn web interface
site: linkedin.com
flavor: managed
forked_from: null
---

# LinkedIn Navigation Skill

## IMPORTANT: Bot Detection

LinkedIn has aggressive bot detection. All interactions MUST include random jitter
delays to avoid detection. Follow the timing rules in this skill carefully.
Use `cc-browser connections open linkedin` to launch the browser, then use cc-browser
commands with the delays specified in the Timing and Delays section below.

## Authentication Check

Logged-in state is indicated by:
- The global nav profile photo: `.global-nav__me-photo`
- The feed identity module: `div.feed-identity-module`

Not-logged-in indicators:
- Login button visible: `a[data-tracking-control-name="guest_homepage-basic_nav-header-signin"]`
- Page body contains "Sign in" text AND no `h1` element present

404 / profile-not-found indicators (check via JavaScript):
- URL contains `/404`
- Body text contains "page not found" or "profile is not available"

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
| Search (groups) | `https://www.linkedin.com/search/results/groups/?keywords={query}` |
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

## Timing and Delays

LinkedIn is extremely sensitive to automation timing. All waits MUST have random
jitter to avoid detection. Never use fixed delays.

### Jitter Strategy (tiered by base delay)

| Base delay | Jitter range | Use case |
|------------|-------------|----------|
| < 1.0s | +0 to 0.5s | Modal waits, quick checks |
| 1.0 - 2.9s | +0 to 1.5s | Click waits, scroll waits |
| >= 3.0s | +0 to 2.0s | Page navigation, page loads |

### Required Delays by Action

| Action | Wait after | Why |
|--------|-----------|-----|
| Navigate to any page | 3-4s + jitter | Full page load, lazy JS init |
| Navigate to profile | 4s + jitter | Profile cards load slowly |
| Click a button | 1-2s + jitter | Action processing |
| Scroll down | 2s + jitter | Lazy content loading |
| Between feed browsing actions | 3-8s random | Human reading simulation |
| Between connection requests | 30-90s random | Anti-spam detection |
| After file/media upload | 2-3s | Processing time |
| Modal dialog interaction | 0.5-1s | Animation completion |
| Every 10 scrolls | Extra 3s pause | Reduce detection risk |

### Human Behavior Simulation

Before performing main tasks, simulate natural browsing:
1. Visit home feed first (navigate, wait 4s)
2. Scroll down once (wait 3-8s random)
3. Maybe scroll again (50% chance, wait 3-6s)
4. Then navigate to actual destination

This "warm-up" makes the session look like a real user.

## Key Elements

LinkedIn uses React. DOM structure changes frequently. Always prefer
snapshot-based element discovery (`cc-browser snapshot --interactive`) over
hardcoded selectors. The selectors below are best-effort as of 2026.

### Profile Page (JavaScript extraction is most reliable)

```javascript
// Name - try specific class first, then any h1
document.querySelector('h1.text-heading-xlarge')?.textContent?.trim()
  || document.querySelector('h1')?.textContent?.trim()

// Headline
document.querySelector('div.text-body-medium')?.textContent?.trim()

// Location
document.querySelector('span.text-body-small.inline')?.textContent?.trim()

// Connection count - look inside profile card li elements
document.querySelector('li span.t-bold')?.textContent?.trim()

// About section - uses anchor-based navigation
document.getElementById('about')  // scroll anchor
  // Then find the section element near it for text content

// Experience section
document.getElementById('experience')  // scroll anchor
  // First entry in section has job title + company

// Education section
document.getElementById('education')  // scroll anchor

// Related profiles (up to 20)
document.querySelectorAll('a[href*="/in/"]')  // all profile links on page
```

### Profile - CSS Selectors

| Element | Selector |
|---------|----------|
| Name (h1) | `h1.text-heading-xlarge` |
| Headline | `div.text-body-medium` |
| Location | `span.text-body-small.inline` |
| Connection count | `span.t-bold` (inside profile card `li`) |
| About anchor | `#about` (section text nearby) |
| Experience anchor | `#experience` |
| Education anchor | `#education` |

### Navigation Bar

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
| Post author | `.update-components-actor__name` |
| Author headline | `.update-components-actor__description` |
| Post time | `span.feed-shared-actor__sub-description` |
| Reactions count | `span.social-details-social-counts__reactions-count` |
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

### Connection Buttons

| Element | Selector |
|---------|----------|
| Connect | `button[aria-label*="Connect"]` |
| Message (already connected) | `button[aria-label*="Message"]` |
| Pending (request sent) | `button[aria-label*="Pending"]` |
| More actions (hidden Connect) | Look for "More actions" dropdown |

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
| Result item (modern) | `main a[href*="/in/"]` with `li` containing `p` elements |
| Result item (legacy) | `li.reusable-search__result-container` |

### Connections Page

| Element | Selector |
|---------|----------|
| Connection link (modern) | `main a[href*="/in/"]` (nested `main main`) |
| Connection card (legacy) | `li.mn-connection-card` |
| Search input (try in order) | See search input cascade below |

Connections search input selector cascade (try in order):
1. `input#mn-connections-search-input`
2. `input[placeholder*="search" i]`
3. `input[placeholder*="Search"]`
4. `input[aria-label*="search" i]`
5. `input[aria-label*="Search"]`

### Invitations

| Element | Selector |
|---------|----------|
| Invitation card | `li.invitation-card` |
| Accept button | `button[aria-label*="Accept"]` |
| Ignore button | `button[aria-label*="Ignore"]` |

### Post Creation

| Element | Selector |
|---------|----------|
| Start a post | Button with "Start a post" text or "share-box" placeholder |
| Post editor | `[role="textbox"][contenteditable="true"]` |
| Media button | Button with media/image icon |
| File input | `input[type="file"]` (hidden, use upload endpoint) |
| Post button | Button with "Post" text |
| Visibility modal Done | Button with "Done" text (may need retries) |

## Workflows

### View a Profile

1. Navigate to `https://www.linkedin.com/in/{username}`
2. Wait 4s + jitter (profiles load slowly)
3. Check for errors via JavaScript:
   - URL contains `/404` -> profile not found
   - Body contains "page not found" -> not found
   - Body contains "Sign in" AND no `h1` -> not logged in
4. Extract data using JavaScript (more reliable than CSS selectors):
   - Name: `h1.text-heading-xlarge` or `h1` fallback
   - Headline: `div.text-body-medium`
   - Location: `span.text-body-small.inline`
   - About: `#about` anchor -> nearby section text
   - Experience: `#experience` anchor -> first entry
   - Education: `#education` anchor -> section content
5. Related profiles: collect all `a[href*="/in/"]` (up to 20 unique)

### Search for People

1. Navigate to search URL with `keywords={query}` and type `people`
2. Wait 3s + jitter
3. Scroll down once, wait 1s (trigger lazy load)
4. Extract results - try modern DOM first, then legacy:
   - **Modern (2024+)**: `main a[href*="/in/"]` -> find parent `li` -> `p` elements
     for name (first paragraph), headline, location
   - **Legacy**: `li.reusable-search__result-container` with `.entity-result-*` classes
5. Parse username from href: `/in/{username}/` -> extract `username`

### List Connections (with dynamic scrolling)

1. Navigate to `/mynetwork/invite-connect/connections`
2. Wait 3s + jitter
3. If filtering: find search input (try selector cascade above), type, press Enter, wait 3s
4. Scroll to load all connections:
   - Count elements: `document.querySelectorAll('main a[href*="/in/"]').length`
   - While count < limit AND stale_counter < 3 AND scroll_count < 50:
     - Scroll down, wait 2s + jitter
     - Recount elements
     - If count unchanged: increment stale counter
     - Every 10 scrolls: extra 3s pause
5. Extract via JavaScript:
   - **Modern**: `main main` (nested) -> `a[href*="/in/"]` -> child `p` elements
     (name = first paragraph, headline = second paragraph)
   - **Legacy**: `li.mn-connection-card` selectors

### Send Connection Request

1. Navigate to `/in/{username}` (profile), wait 3s
2. Scroll down (simulate reading), wait 8-20s random
3. Find Connect button:
   - Look for `button[aria-label*="Connect"]`
   - If not visible: check "More actions" dropdown for hidden Connect
   - If "Pending" visible: request already sent
   - If "Message" visible (no "Follow"): already connected
4. Click Connect button, wait 1s
5. If note dialog appears: click "Add a note", type note, click send/done
6. Wait 30-90s random before next connection request

### Send a Message

1. Navigate to `/in/{username}` (profile), wait 3s
2. Find Message button (indicates already connected)
3. Click Message, wait 2s
4. Find message input (textbox or contenteditable)
5. Click input, wait 0.3s
6. Type message text, wait 0.5s
7. Find send button OR press Enter to send

### Read Messages

1. Navigate to `https://www.linkedin.com/messaging`
2. Wait 3s + jitter
3. Thread list: `div.msg-conversation-card` elements
4. Click a thread to open it, wait 1-2s
5. Messages are in `div.msg-s-event-listitem__body`

### Like a Post

1. Navigate to post URL, wait 3s
2. Get interactive snapshot
3. Find Like button: `button[aria-label*="Like"]`
4. Click, wait 0.5-1s

### Comment on a Post

1. Navigate to post URL, wait 3s
2. Find Comment button, click it, wait 1s
3. Find comment input field
4. Click input, type comment text
5. Find submit button, click, wait 0.5-1s

### Create a Post

1. Navigate to `/feed/`, wait 2s
2. Click "Start a post" button
3. Find `[role="textbox"][contenteditable="true"]`
4. Type content (escape quotes/newlines in JS evaluation), wait 1s
5. If attaching image:
   - Click media button, wait 2s
   - Find `input[type="file"]`, use upload endpoint, wait 3s
   - Click "Next" if it appears
6. Click "Post" button, wait 3s
7. Handle visibility modal: try clicking "Done" button up to 10 times (0.5s each)
   - If "Done" appears, click it, wait 2s, then click "Post" again

### Browse Network Suggestions

1. Simulate human browsing first (visit feed, scroll)
2. Navigate to `/mynetwork/grow`, wait 4s
3. Scroll down twice (wait 2s each) to load suggestions
4. Extract suggestions via JavaScript:
   - `main main` -> `a[href*="/in/"]` -> child `p` elements (name, headline)
   - Each suggestion has a parent `li` with a button containing "connect"
5. Clean names: strip badges (", Verified", ", Premium", ", Influencer")
   using regex: `,?\s*(Verified|Premium|Influencer|Ver)\b.*$`

## Gotchas

### Bot Detection
LinkedIn actively detects automation. Critical rules:
- NEVER use fixed delays -- always add random jitter
- NEVER navigate to more than ~5 profiles rapidly
- ALWAYS simulate human warm-up (visit feed first)
- Space connection requests 30-90s apart
- Pause every 10 scrolls with extra 3s delay
- If you see a captcha or "unusual activity" message, STOP immediately

### DOM Structure Instability
LinkedIn updates their DOM frequently. Key defensive strategies:
- Use JavaScript `document.querySelector()` for extraction -- more reliable
  than snapshot ref-based approaches for data extraction
- Always try modern (2024+) selectors first, then legacy fallbacks
- For connections: DOM changed from `li.mn-connection-card` to nested
  `main main a[href*="/in/"]` with `p` elements
- For search: changed from `li.reusable-search__result-container` to
  `main a[href*="/in/"]` with `li` and `p` parents

### Snapshot Refs Are Transient
Element refs from `cc-browser snapshot --interactive` are valid only for the
current DOM state. After any action (click, scroll, navigate), you MUST get
a new snapshot to get fresh refs.

### Name Parsing Pitfalls
- Names may include badge text: "John Smith, Verified" or "Jane Doe, Premium"
- `innerText` can repeat the name on multiple lines due to hidden badges
- Always split on `\n` and take the first element
- Exclude false positives: "Connect", "Follow", "Message", "Pending", "Show all"
  are button labels, not names

### Connection Button False Positives
When looking for Connect buttons, exclude:
- "Connection Management A/S" (company name containing "connect")
- "500+ connections" (profile stat text)
- "connected" / "disconnect" variants
- Education/company section buttons that happen to match

### Hidden File Inputs
`input[type="file"]` elements are hidden and cannot be directly clicked.
Use the cc-browser upload endpoint instead: mark the element with a data
attribute via JavaScript, then call the upload API with that selector.

### Modal Retry Pattern
Some modals (like post visibility) require retrying the click. Pattern:
try clicking "Done" button up to 10 times with 0.5s waits between attempts.
If modal appears, click Done, wait 2s, then click Post again.

### Login Walls
Many pages redirect to login if the session expires. Before any workflow,
verify authentication by checking for the login button selector or absence
of the global nav profile photo.

### Dynamic Content Loading
- Feed content loads lazily on scroll (initial snapshot shows few posts)
- Connections list requires scrolling to load all entries (up to 50 scroll cycles)
- Search results may need a scroll to trigger lazy loading
- Use the stale counter pattern: 3 consecutive scrolls with no new elements = stop
