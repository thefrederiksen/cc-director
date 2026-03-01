"""Scroll through Liked Songs and collect all tracks.

Uses slow, patient scrolling to let Spotify's virtual list fully render each batch.
"""

import json
import sys
import time
sys.path.insert(0, str(__import__("pathlib").Path(__file__).resolve().parent.parent))

from src.browser_client import BrowserClient

EXTRACT_JS = """
(() => {
    const rows = document.querySelectorAll('[data-testid="tracklist-row"]');
    const tracks = [];
    rows.forEach((row) => {
        const nameEl = row.querySelector('[data-testid="internal-track-link"]');
        const name = nameEl ? nameEl.textContent.trim() : "";
        const artistLinks = row.querySelectorAll('a[href*="/artist/"]');
        const artists = [];
        artistLinks.forEach(a => {
            const text = a.textContent.trim();
            if (text && !artists.includes(text)) artists.push(text);
        });
        const albumLink = row.querySelector('a[href*="/album/"]');
        const album = albumLink ? albumLink.textContent.trim() : "";
        const ariaIdx = row.getAttribute("aria-rowindex") || "";
        if (name) tracks.push({idx: ariaIdx, name: name, artist: artists.join(", "), album: album});
    });
    return JSON.stringify(tracks);
})()
"""

# Scroll by one viewport height (clientHeight)
SCROLL_ONE_PAGE_JS = """
(() => {
    const child = document.querySelector('.main-view-container__scroll-node-child');
    const c = child ? child.parentElement : null;
    if (!c) return JSON.stringify({error: "No scroll container"});
    c.scrollBy(0, Math.floor(c.clientHeight * 0.5));
    return JSON.stringify({
        scrollTop: c.scrollTop,
        scrollHeight: c.scrollHeight,
        clientHeight: c.clientHeight,
        atBottom: c.scrollTop + c.clientHeight >= c.scrollHeight - 20
    });
})()
"""

SCROLL_TO_TOP_JS = """
(() => {
    const child = document.querySelector('.main-view-container__scroll-node-child');
    const c = child ? child.parentElement : null;
    if (!c) return JSON.stringify({error: "No scroll container"});
    c.scrollTop = 0;
    return JSON.stringify({scrollTop: c.scrollTop});
})()
"""


def collect_visible(client, all_tracks):
    """Extract currently visible tracks and merge into all_tracks dict."""
    result = client.evaluate(EXTRACT_JS)
    raw = result.get("result", "[]")
    data = json.loads(raw) if isinstance(raw, str) else raw
    for t in data:
        key = t.get("idx") or (t["name"] + "|" + t["artist"])
        if key and key not in all_tracks:
            all_tracks[key] = t


client = BrowserClient(workspace="chrome-personal")
all_tracks = {}

# Scroll to top
client.evaluate(SCROLL_TO_TOP_JS)
time.sleep(1.0)

print("Scrolling one page at a time...")

for scroll_num in range(500):
    # Wait for virtual list to render
    time.sleep(1.2)

    # Collect what's visible
    collect_visible(client, all_tracks)
    count = len(all_tracks)

    if scroll_num % 5 == 0:
        print(f"  Page {scroll_num}: {count} tracks collected")

    # Scroll one page
    result = client.evaluate(SCROLL_ONE_PAGE_JS)
    raw = result.get("result", "{}")
    data = json.loads(raw) if isinstance(raw, str) else {}

    if data.get("atBottom"):
        # At bottom -- collect one more time after a wait
        time.sleep(1.5)
        collect_visible(client, all_tracks)
        count = len(all_tracks)
        print(f"  Reached bottom at page {scroll_num}: {count} tracks")
        break

# Sort by aria-rowindex
sorted_tracks = sorted(all_tracks.values(), key=lambda t: int(t.get("idx") or "0"))

print(f"\nTotal unique liked songs: {len(sorted_tracks)}")
print()
for i, t in enumerate(sorted_tracks, 1):
    print(f"  {i:>3}. {t['name']}  --  {t['artist']}  [{t['album']}]")
