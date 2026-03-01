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
    """Extract library items from sidebar.

    Spotify sidebar uses nav[aria-label='Main'] with div[role='row'] items.
    Each row has a title span (class contains 'ListRowTitle') and subtitle text.
    """
    return """
    (() => {
        const nav = document.querySelector('nav[aria-label="Main"]');
        if (!nav) return JSON.stringify([]);
        const rows = nav.querySelectorAll('[role="row"]');
        const items = [];
        rows.forEach((row) => {
            const idx = row.getAttribute("aria-rowindex") || "";
            const titleEl = row.querySelector('[class*="ListRowTitle"]');
            const name = titleEl ? titleEl.textContent.trim() : "";
            if (!name) return;
            const fullText = row.textContent.trim();
            const subtitle = fullText.startsWith(name) ?
                fullText.substring(name.length).trim() : "";
            let itemType = "";
            if (subtitle.startsWith("Playlist")) itemType = "playlist";
            else if (subtitle.startsWith("Podcast")) itemType = "podcast";
            else if (subtitle.startsWith("Artist")) itemType = "artist";
            else if (subtitle.startsWith("Album") || subtitle.includes("album")) itemType = "album";
            else if (subtitle.startsWith("Single")) itemType = "album";
            else if (subtitle.includes("Pinned")) itemType = "playlist";
            items.push({name: name, index: parseInt(idx) || items.length, type: itemType, subtitle: subtitle});
        });
        return JSON.stringify(items);
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


def get_tracklist_rows_js() -> str:
    """Extract all currently rendered tracklist rows with name, artist, album."""
    return """
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


def scroll_main_view_js(pixels: int = 3000) -> str:
    """Scroll the main content area by given pixels."""
    return f"""
    (() => {{
        const child = document.querySelector('.main-view-container__scroll-node-child');
        const c = child ? child.parentElement : null;
        if (!c) return JSON.stringify({{error: "No scroll container"}});
        c.scrollBy(0, {pixels});
        return JSON.stringify({{scrollTop: c.scrollTop, scrollHeight: c.scrollHeight, clientHeight: c.clientHeight}});
    }})()
    """


def scroll_main_view_to_js(position: int) -> str:
    """Scroll the main content area to an absolute position."""
    return f"""
    (() => {{
        const child = document.querySelector('.main-view-container__scroll-node-child');
        const c = child ? child.parentElement : null;
        if (!c) return JSON.stringify({{error: "No scroll container"}});
        c.scrollTop = {position};
        return JSON.stringify({{scrollTop: c.scrollTop, scrollHeight: c.scrollHeight}});
    }})()
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
