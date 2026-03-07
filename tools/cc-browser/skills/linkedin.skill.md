---
name: linkedin
version: 2026.03.06.3
description: Navigate LinkedIn web interface
site: linkedin.com
flavor: managed
forked_from: null
---

# LinkedIn Navigation Skill

## Workflow Status

Each workflow in this skill has a maturity level based on live-site validation.

| Status | Meaning |
|--------|---------|
| Tested | Proven end-to-end on the live site. Date noted. |
| Partial | Some parts tested, some not. Notes explain what's missing. |
| Untested | Written from DOM knowledge but never validated on live site. |

| Workflow | Status | Notes |
|----------|--------|-------|
| Send a Message | Partial (2026-03-06) | Path A (profile -> Message button -> existing thread) proven. Path B (compose new) proven. Attachment workflow untested. |
| View a Profile | Untested | |
| Search for People | Untested | |
| List Connections | Untested | |
| Send Connection Request | Untested | |
| Read Messages | Untested | |
| Like a Post | Untested | |
| Comment on a Post | Untested | |
| Create a Post | Untested | |
| Browse Network Suggestions | Untested | |
| Batch Sending from Queue | Partial (2026-03-06) | Single send from queue proven. Multi-send loop untested. |

**Caution for Untested workflows:** Screenshot every step, expect selectors to be wrong, and be ready to adapt. If a selector fails, inspect the page and update this skill with what actually works. Tested/Partial workflows can be followed with confidence.

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

| Element | Selector / Discovery |
|---------|---------------------|
| Search messages box | snapshot: `searchbox "Search messages"` |
| Compose new message button | snapshot: button with text "Compose a new message" |
| Recipient input (new message) | snapshot: `combobox "Type a name or multiple names"` |
| Thread card | `div.msg-conversation-card` |
| Message input | `div.msg-form__contenteditable` |
| Send button | `button.msg-form__send-button` (use snapshot ref to click -- `--text "Send"` is unreliable) |
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

### Text Input on LinkedIn (contentEditable)

LinkedIn message boxes and post editors use contentEditable divs (React/Draft.js),
not regular `<input>` or `<textarea>` elements.

**Use `paste` for contentEditable fields** -- this is the only reliable method:
```
cc-browser -c linkedin paste --selector "div.msg-form__contenteditable" --text "Your message here"
```
The paste command creates a synthetic ClipboardEvent in MAIN world, which React editors
handle correctly. It preserves newlines (converted to `<br>` tags).

**NEVER use `type`** on contentEditable -- it sends characters one-by-one and breaks mid-stream.
**NEVER use `evaluate`** on LinkedIn -- CSP blocks unsafe-eval.
**`fill` works for regular inputs** (search boxes, login fields) but is unreliable for
contentEditable editors. Use `paste` for message composition and post creation.

### Send a Message

All commands require `--connection linkedin`. Abbreviated as `-c linkedin` below.

There are THREE paths depending on situation:
- **Path A (Direct)**: You have the profile URL -> navigate to profile -> click "Message {Name}" button (opens correct thread directly)
- **Path B (Search)**: No profile URL -> go to /messaging/ -> search for existing conversation
- **Path C (Compose)**: No existing conversation found via search -> use "Compose a new message"

When sending from the comm queue, prefer Path A (queue items include the profile URL).
When you don't have a profile URL, use Path B, falling back to Path C if search returns nothing.

**Step 0 -- Open browser with retry:**
1. Open fresh browser: `cc-browser connections open linkedin`
2. Wait 5s, check connection: `cc-browser connections status`
3. If status shows disconnected: `cc-browser connections close linkedin`, wait 2s, `cc-browser connections open linkedin`, wait 10s, check status again
4. If still disconnected after retry, STOP and report to user

**Screenshot fallback:** If `screenshot` fails with "image readback failed", use
`cc-browser -c linkedin snapshot --interactive` instead. Snapshots show element names,
text content, and refs -- sufficient to verify recipient name, message text in textbox,
and Send button ref. Snapshot-based verification is equivalent to screenshot verification.

**Path A -- Direct from profile (preferred when you have profile URL):**
1. Navigate to profile: `cc-browser -c linkedin navigate "https://www.linkedin.com/in/{username}"`
2. Wait 4s + jitter
3. Click the Message button on profile: `cc-browser -c linkedin click --text "Message {FirstName}"`
   - The button text is "Message {FirstName}" (e.g., "Message Bohdan")
   - ALWAYS use the full text "Message {FirstName}", NEVER just "Message" -- the short text
     matches nav elements and silently clicks the wrong thing
   - Extract first name from the message content (first line is usually "{FirstName},")
     or from `destination_url`/`notes` field
   - This opens a **chat overlay** at the bottom-right of the page (NOT full-page navigation)
4. Wait 3s + jitter
5. Snapshot to verify correct conversation overlay opened (look for the recipient name in overlay header)
6. If other conversation overlays are already open, close them first:
   - Look for `text "Close your conversation with..."` for the OTHER conversations
   - Click to close them so only the target conversation remains
7. Continue to "Compose and Send" below

**Path B -- Search existing conversations (when no profile URL):**
1. Navigate to messaging: `cc-browser -c linkedin navigate "https://www.linkedin.com/messaging/"`
2. Wait 3s + jitter
3. Snapshot to find search box: `cc-browser -c linkedin snapshot --interactive 2>&1 | grep -i "search"`
   - Look for `searchbox "Search messages" [ref=eXX]`
4. Click search box BY REF: `cc-browser -c linkedin click --ref eXX`
5. Wait 1s, type contact name: `cc-browser -c linkedin type --selector "input[type='search'], input[name='searchTerm'], .msg-search-form input" --text "ContactName"`
6. Press Enter: `cc-browser -c linkedin press --key Enter`
7. Wait 3s + jitter, then screenshot/snapshot -- check results
8. If conversation found: click their conversation: `cc-browser -c linkedin click --text "ContactName"`
9. Wait 2s + jitter, verify correct person's header name
10. Continue to "Compose and Send" below
11. If "We didn't find anything" -- switch to Path C

**Path C -- Compose new message (no existing conversation):**
1. Snapshot to find compose button: look for button with text "Compose a new message"
2. Click compose BY REF: `cc-browser -c linkedin click --ref eXX`
3. Wait 2s, screenshot/snapshot -- verify "New message" dialog with "Type a name or multiple names" input
4. Snapshot to find recipient input: look for `combobox "Type a name or multiple names" [ref=eXX]`
5. Click recipient input BY REF, then type contact name: `cc-browser -c linkedin type --ref eXX --text "ContactName"`
6. Wait 3s for dropdown to populate, then screenshot/snapshot
7. Select the correct person from dropdown (verify connection degree and headline)
   Click their name or headline text: `cc-browser -c linkedin click --text "Their headline text"`
8. Wait 2s, verify green name pill appears in recipient field
9. Continue to "Compose and Send" below

**Compose and Send (all paths converge here):**
1. Click the message textbox to focus (use ref from snapshot -- there may be multiple textboxes if overlays stacked):
   `cc-browser -c linkedin click --ref eXX`
2. Wait 1s, paste message: `cc-browser -c linkedin paste --selector "div.msg-form__contenteditable" --text "Your message"`
   NOTE: `paste` requires `--selector`, NOT `--ref`. It does not accept `--ref`.
   NOTE: If multiple overlays are open, close others first so there is only ONE `div.msg-form__contenteditable`.
3. Snapshot to find Send button ref: look for `button "Send" [ref=eXX]`
4. Click Send BY REF: `cc-browser -c linkedin click --ref eXX`
   IMPORTANT: `click --text "Send"` is unreliable -- always use `--ref` for Send
5. Wait 3s, screenshot/snapshot -- verify message sent:
   - Message appears in conversation thread
   - Editor is empty ("Write a message..." placeholder visible)
   - No error messages

User approval is NOT required before sending -- items in the queue have already been
approved. Send immediately after pasting and verifying the recipient is correct.

**Critical rules:**
- ALWAYS click Send by ref (from snapshot), never by text -- `--text "Send"` is unreliable
- `paste` requires `--selector`, not `--ref` -- these are different cc-browser commands
- Reuse browser session for batch sends -- just close each overlay after sending
- Close other conversation overlays before pasting to avoid targeting wrong textbox
- If screenshot fails with "image readback failed", use snapshot instead -- snapshots are sufficient
- If any step fails silently, take a screenshot/snapshot to diagnose before retrying
- Queue items are pre-approved -- send immediately, no user confirmation needed

### Batch Sending from Queue

Complete step-by-step for sending approved messages from the communication queue:

1. **List approved items:** `cc-comm-queue list --status approved`
2. **Open browser once:** `cc-browser connections open linkedin` (with retry per Step 0 above)
3. **For each message:**
   a. `cc-comm-queue show <id> --json` -- get details
      - Extract `destination_url` (or `recipient.profile_url`) for profile navigation
      - Extract first name from `content` (first line is typically "{FirstName},")
      - Extract `content` for the message text
   b. Navigate to profile URL
   c. Click "Message {FirstName}" -- opens chat overlay
   d. Close any other open overlays (snapshot, look for "Close your conversation with...")
   e. Focus textbox (by ref), paste message content
   f. Snapshot for Send button ref, click Send by ref
   g. Verify sent (snapshot: "sent the following messages" + empty textbox)
   h. Close the chat overlay: click "Close your conversation with {Name}" text
   i. `cc-comm-queue mark-posted <id>`
   j. Wait 3-5s before next message (human-like pacing)
4. **If send fails:** diagnose, fix, retry before moving to next
5. **Close browser when done:** `cc-browser connections close linkedin`

Reusing the same browser session is fine -- just close each chat overlay after sending.
No need to close/reopen browser between messages. If the session gets into a bad state
(stale overlays, errors), close and reopen the browser as recovery.

### Failure Handling

During any LinkedIn workflow (especially batch sends), watch for these failure cases.
Detect them via URL checks, snapshot text, or command output after each step.

**1. Profile 404**
- Detection: After navigation, URL contains `/404` or snapshot shows "page not found"
  or "profile is not available".
- Recovery: Skip this item, log the error with the profile URL. Continue to next item.

**2. Not connected**
- Detection: Snapshot shows no "Message {FirstName}" button. Instead, only "Connect"
  or "Follow" buttons are visible on the profile.
- Recovery: Skip this item. You cannot send a message without an existing connection.
  Log which contact was skipped and why.

**3. Paste failed**
- Detection: `paste` command returns `pasted: false`, or a snapshot after pasting shows
  the textbox is still empty (placeholder text "Write a message..." still visible).
- Recovery: Retry once -- click the textbox again (by ref), wait 1s, then re-paste.
  If it fails a second time, skip and log the error.

**4. Send button not found**
- Detection: Snapshot after opening the message overlay has no `button "Send"` element.
  The chat overlay may not have opened properly.
- Recovery: Close any open overlay (click "Close your conversation with..." if present),
  wait 2s, then retry by clicking "Message {FirstName}" on the profile again. If it
  fails a second time, skip and log the error.

**5. Rate limiting**
- Detection: Snapshot or page text contains "you've reached the limit",
  "you've hit a limit", "too many requests", or any other unusual restriction text.
- Recovery: STOP the entire batch immediately. Do NOT continue sending. Report the
  exact message text to the user. Wait for user instructions before resuming.

**6. Session expired**
- Detection: After navigation, URL contains "login" or "authwall", or the page shows
  a login form instead of the expected content.
- Recovery: STOP the entire batch immediately. Report to the user that the LinkedIn
  session has expired. The user must re-authenticate before any further actions.

**7. Chat overlay targets wrong person**
- Detection: After clicking "Message {FirstName}", snapshot shows a different name
  in the chat overlay header than the intended recipient.
- Recovery: Close the overlay (click "Close your conversation with..." for the wrong
  person), wait 2s, then retry clicking "Message {FirstName}" on the profile. If
  the wrong person appears again, skip this item and log the error.

**Batch size guidance:** Send no more than 20 messages per session. Insert 3-5s delays
between each send (in addition to normal jitter delays). If you need to send more,
close the browser, wait several minutes, then start a new session for the next batch.

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

### Messaging: Search vs Compose
"Search messages" only searches EXISTING conversations. If you have never messaged
someone before, search returns "We didn't find anything with [name]". You MUST use
the "Compose a new message" button (pencil icon next to search) to start a new thread.
Always check the search results screenshot before assuming the contact was found.

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
