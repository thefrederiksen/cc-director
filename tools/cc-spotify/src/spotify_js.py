"""JavaScript snippets for extracting data from Spotify Web Player DOM.

Each function returns a JS string to be passed to browser.evaluate().
The JS should return JSON-serializable data.
"""


def get_now_playing_js() -> str:
    """Extract current track info from the now-playing bar."""
    return """
    (() => {
        const bar = document.querySelector('[data-testid="now-playing-widget"]');
        if (!bar) return JSON.stringify({error: "No now-playing widget found"});

        const trackLink = bar.querySelector('[data-testid="context-item-link"]');
        const trackName = trackLink ? trackLink.textContent.trim() : "";

        const subtitles = bar.querySelector('[data-testid="context-item-info-subtitles"]');
        const artist = subtitles ? subtitles.textContent.trim() : "";

        const pos = document.querySelector('[data-testid="playback-position"]');
        const dur = document.querySelector('[data-testid="playback-duration"]');
        const position = pos ? pos.textContent.trim() : "";
        const duration = dur ? dur.textContent.trim() : "";

        const playBtn = document.querySelector('[data-testid="control-button-playpause"]');
        const isPlaying = playBtn ?
            playBtn.getAttribute("aria-label") === "Pause" : false;

        const likeBtn = bar.closest('[data-testid="now-playing-bar"]')
            ?.querySelector('[data-testid="add-button"]');
        const isLiked = likeBtn ?
            likeBtn.getAttribute("aria-checked") === "true" : false;

        return JSON.stringify({
            name: trackName,
            artist: artist,
            position: position,
            duration: duration,
            is_playing: isPlaying,
            is_liked: isLiked
        });
    })()
    """


def get_shuffle_state_js() -> str:
    """Check if shuffle is active."""
    return """
    (() => {
        const btn = document.querySelector('[data-testid="control-button-shuffle"]');
        if (!btn) return JSON.stringify({shuffle: false, error: "Shuffle button not found"});
        const checked = btn.getAttribute("aria-checked") === "true";
        return JSON.stringify({shuffle: checked});
    })()
    """


def get_repeat_state_js() -> str:
    """Check current repeat mode."""
    return """
    (() => {
        const btn = document.querySelector('[data-testid="control-button-repeat"]');
        if (!btn) return JSON.stringify({repeat: "off", error: "Repeat button not found"});
        const label = btn.getAttribute("aria-label") || "";
        let mode = "off";
        if (label.toLowerCase().includes("disable repeat")) mode = "context";
        else if (label.toLowerCase().includes("disable")) mode = "track";
        else if (label.toLowerCase().includes("enable repeat one")) mode = "context";
        else if (label.toLowerCase().includes("enable")) mode = "off";
        // Also check aria-checked
        const checked = btn.getAttribute("aria-checked");
        if (checked === "false") mode = "off";
        return JSON.stringify({repeat: mode});
    })()
    """


def get_playlists_js() -> str:
    """Extract playlist names from sidebar."""
    return """
    (() => {
        const items = document.querySelectorAll('[data-testid="rootlist-item"]');
        const playlists = [];
        items.forEach((item, i) => {
            const nameEl = item.querySelector('[data-testid="listrow-title"]') ||
                          item.querySelector('span');
            const name = nameEl ? nameEl.textContent.trim() : "";
            if (name) playlists.push({name: name, index: i});
        });
        return JSON.stringify(playlists);
    })()
    """


def get_search_results_js() -> str:
    """Extract search results from the search page."""
    return """
    (() => {
        const results = [];

        // Top result
        const topCard = document.querySelector('[data-testid="top-result-card"]');
        if (topCard) {
            const name = topCard.querySelector('a')?.textContent?.trim() || "";
            const subtitle = topCard.querySelector('span')?.textContent?.trim() || "";
            if (name) results.push({name: name, artist: subtitle, type: "top_result"});
        }

        // Track results
        const trackRows = document.querySelectorAll(
            '[data-testid="search-tracks-result"] [data-testid="tracklist-row"]'
        );
        trackRows.forEach(row => {
            const nameEl = row.querySelector('[data-testid="internal-track-link"]');
            const name = nameEl ? nameEl.textContent.trim() : "";
            // Artist is typically in a span/link after the track name
            const cells = row.querySelectorAll('span, a');
            let artist = "";
            cells.forEach(cell => {
                const text = cell.textContent.trim();
                if (text && text !== name && !text.includes(":") && text.length < 100) {
                    if (!artist) artist = text;
                }
            });
            if (name) results.push({name: name, artist: artist, type: "track"});
        });

        return JSON.stringify(results);
    })()
    """


def get_queue_js() -> str:
    """Extract tracks from the queue view."""
    return """
    (() => {
        const rows = document.querySelectorAll('[data-testid="tracklist-row"]');
        const tracks = [];
        rows.forEach((row, i) => {
            const nameEl = row.querySelector('[data-testid="internal-track-link"]') ||
                          row.querySelector('a[href*="/track/"]');
            const name = nameEl ? nameEl.textContent.trim() : "";
            const spans = row.querySelectorAll('span, a');
            let artist = "";
            spans.forEach(s => {
                const text = s.textContent.trim();
                if (text && text !== name && !text.includes(":") && text.length < 80) {
                    if (!artist && s.closest('a[href*="/artist/"]')) artist = text;
                }
            });
            if (name) tracks.push({name: name, artist: artist, position: i + 1});
        });
        return JSON.stringify(tracks);
    })()
    """


def set_volume_js(level: int) -> str:
    """Generate JS to set volume slider to a specific level (0-100)."""
    return f"""
    (() => {{
        const volumeBar = document.querySelector('[data-testid="volume-bar"]');
        if (!volumeBar) return JSON.stringify({{error: "Volume bar not found"}});

        const input = volumeBar.querySelector('input[type="range"]');
        if (!input) return JSON.stringify({{error: "Volume slider input not found"}});

        // Set value via native setter to trigger React state update
        const nativeSetter = Object.getOwnPropertyDescriptor(
            window.HTMLInputElement.prototype, 'value'
        ).set;
        nativeSetter.call(input, {level});

        // Dispatch events to notify React
        input.dispatchEvent(new Event('input', {{ bubbles: true }}));
        input.dispatchEvent(new Event('change', {{ bubbles: true }}));

        return JSON.stringify({{volume: {level}, success: true}});
    }})()
    """
