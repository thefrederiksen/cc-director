# cc-spotify: Browser Automation Learnings

Notes from building cc-spotify -- a CLI that controls Spotify Web Player via cc-browser daemon automation. These patterns apply to any browser automation project using Playwright/CDP.

---

## 1. Virtual List Scrolling

Spotify (and many modern web apps) uses a **virtual list** that only renders ~20-25 DOM rows at a time. Rows mount/unmount as the user scrolls.

**The problem**: You can't just grab all DOM elements at once. You need to scroll through the list, collecting visible rows at each position.

**Best approach**: Scroll in small increments with pauses between each scroll to let the virtual list render. Deduplicate by `aria-rowindex` (stable row identifier).

**Coverage ceiling**: ~96% (706/733 tracks). Some rows exist in the transition gap between rendered batches and never appear in the DOM during any single scroll position. This is a fundamental limitation of DOM scraping virtual lists -- no scroll method fixes it.

**What we tried**:

| Method | Speed | Coverage | Notes |
|--------|-------|----------|-------|
| Big JS jumps (3000px) | ~1 min | ~92% | Misses tracks in transition zones |
| Half-page JS scrollBy | ~2 min | 96.3% | Best speed/coverage tradeoff |
| Mouse wheel (200px ticks) | ~6 min | 96.3% | Same coverage, more realistic |
| Mouse wheel (300px ticks) | ~4 min | 96.3% | Same coverage |

---

## 2. Finding the Right Scroll Container

Spotify has multiple scrollable containers. The class names are misleading:

- `.main-view-container__scroll-node` -- NOT scrollable (scrollHeight == clientHeight)
- `[data-overlayscrollbars-viewport]` -- This is the LEFT SIDEBAR, not the main content
- The actual scroll container: unnamed `<div>` that is the **parent** of `.main-view-container__scroll-node-child`, with `overflowY: scroll`

**How to find it**: Walk up from a known content element (like `[data-testid="tracklist-row"]`) and check `scrollHeight > clientHeight`.

```javascript
const child = document.querySelector('.main-view-container__scroll-node-child');
const container = child ? child.parentElement : null;
// container.scrollHeight: 41966, container.clientHeight: 505
```

**Lesson**: Always verify scroll containers by checking `scrollHeight > clientHeight`. Don't trust class names.

---

## 3. Mouse Wheel Scrolling (page.mouse.wheel)

`page.mouse.wheel(deltaX, deltaY)` dispatches wheel events **at the current Playwright cursor position**.

**Critical**: If the cursor isn't over the target scroll container, the wheel events go to whatever element IS under the cursor (or nowhere).

**Solution**: Hover on an element inside the target area first:

```python
# 1. Take accessibility snapshot to find a ref in the content area
snap = client.snapshot()  # Returns text like: '- link "Track Name" [ref=e78]'

# 2. Hover to position cursor
client._post("/hover", {"ref": "e78"})

# 3. NOW wheel scrolling works on the content area
client.scroll("down", amount=200)
```

**What doesn't work**:
- JS `dispatchEvent(new MouseEvent('mousemove', ...))` does NOT move the Playwright cursor
- JS `dispatchEvent(new WheelEvent('wheel', ...))` does NOT scroll Spotify's custom container
- These are separate event systems -- JS events don't affect Playwright's internal cursor state

**cc-browser daemon human mode**: When scrolling, the daemon breaks each wheel call into 3-6 smaller steps with random 30-100ms delays between them, simulating real mouse wheel behavior.

---

## 4. Spotify Selector Patterns

Spotify uses `data-testid` attributes on many elements but the sidebar changed significantly:

**Stable selectors** (still work):
- `[data-testid="tracklist-row"]` -- track rows in any list
- `[data-testid="internal-track-link"]` -- track name link
- `a[href*="/artist/"]` -- artist links
- `a[href*="/album/"]` -- album links
- `[data-testid="now-playing-widget"]` -- now playing bar
- `[data-testid="control-button-playpause"]` -- play/pause
- `[data-testid="volume-bar"]` -- volume slider

**Broken selectors** (Spotify changed their sidebar):
- `[data-testid="rootlist-item"]` -- no longer exists
- `[data-testid="listrow-title"]` -- no longer exists

**New sidebar structure**:
- Container: `nav[aria-label="Main"]`
- Items: `div[role="row"]` with `aria-rowindex`
- Title: `span[class*="ListRowTitle"]` (styled-components class, prefix is stable)
- Subtitle: remaining text content after title

---

## 5. Accessibility Snapshot Format

The cc-browser `/snapshot` endpoint returns a **text-based accessibility tree**, not a JSON DOM tree:

```
- link "Skip to main content" [ref=e1]
- button "Home" [ref=e3]
- link "Knock On Wood" [ref=e78]
- link "Safri Duo" [ref=e79]
```

Refs like `e78` can be used with `/click`, `/hover`, and other daemon endpoints. Parse with regex:

```python
import re
for line in snapshot_text.split("\n"):
    m = re.search(r'link ".+?" \[ref=(e\d+)\]', line)
    if m:
        ref = m.group(1)  # "e78"
```

---

## 6. Three Interaction Methods

| Method | When to use | Reliability |
|--------|------------|-------------|
| **Keyboard shortcuts** | Playback (Space, Shift+Arrow) | Most stable -- survives UI redesigns |
| **JS evaluation** | Reading DOM data | Good for extraction, fragile if selectors change |
| **Snapshot + click/hover** | Buttons without shortcuts | Uses accessibility tree, avoids CSS selectors |

---

## 7. Volume Slider Automation

React controls native input value setter. To change a slider:

```javascript
const nativeSetter = Object.getOwnPropertyDescriptor(
    window.HTMLInputElement.prototype, 'value'
).set;
nativeSetter.call(input, 75);  // Set to 75%
input.dispatchEvent(new Event('input', { bubbles: true }));
input.dispatchEvent(new Event('change', { bubbles: true }));
```

This bypasses React's synthetic event system and triggers the actual state update.

---

## 8. Workspace Configuration

cc-browser uses `{browser}-{workspace}` directory naming:
- `chrome-personal` -> browser=chrome, workspace=personal
- Daemon reads `workspace.json` for `daemonPort`, `cdpPort`, profile settings
- Multiple workspaces can run simultaneously on different ports
- cc-spotify stores its default workspace in `CcStorage.tool_config("spotify")/config.json`
