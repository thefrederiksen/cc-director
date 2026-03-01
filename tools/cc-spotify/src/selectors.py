"""CSS selectors and data-testid attributes for Spotify Web Player.

WARNING: Spotify changes their UI frequently. These selectors may need updates.
Use `cc-spotify status --verbose` or `cc-spotify now --verbose` to dump raw
snapshot data for debugging broken selectors.

Strategy:
    1. Keyboard shortcuts (most reliable, survive UI redesigns)
    2. data-testid attributes (semi-stable)
    3. aria-label attributes (fallback)
"""


# =============================================================================
# Keyboard Shortcuts (Spotify Web Player)
# =============================================================================

class SpotifyKeys:
    """Keyboard shortcuts for Spotify Web Player.

    These are the most reliable interaction method -- they survive UI redesigns.
    Reference: Spotify keyboard shortcuts help page.
    """

    PLAY_PAUSE = "Space"
    NEXT_TRACK = "Shift+ArrowRight"
    PREV_TRACK = "Shift+ArrowLeft"
    SEEK_FORWARD = "Shift+ArrowRight"       # +5 seconds (same as next in some contexts)
    SEEK_BACKWARD = "Shift+ArrowLeft"       # -5 seconds
    VOLUME_UP = "ArrowUp"                    # Only when volume slider focused
    VOLUME_DOWN = "ArrowDown"                # Only when volume slider focused
    SHUFFLE = "s"                            # Toggle shuffle
    REPEAT = "r"                             # Cycle repeat modes
    LIKE = "Alt+Shift+b"                     # Save to Liked Songs


# =============================================================================
# CSS Selectors (data-testid based)
# =============================================================================

class SpotifySelectors:
    """Selectors for Spotify Web Player elements.

    Prefer data-testid attributes where available. These are more stable
    than class names but can still change.
    """

    # --- Now Playing Bar (bottom) ---
    NOW_PLAYING_BAR = '[data-testid="now-playing-bar"]'
    NOW_PLAYING_WIDGET = '[data-testid="now-playing-widget"]'
    TRACK_NAME = '[data-testid="context-item-link"]'
    ARTIST_NAME = '[data-testid="context-item-info-subtitles"]'
    ALBUM_ART = '[data-testid="cover-art-image"]'
    PLAYBACK_POSITION = '[data-testid="playback-position"]'
    PLAYBACK_DURATION = '[data-testid="playback-duration"]'

    # --- Playback Controls ---
    PLAY_BUTTON = '[data-testid="control-button-playpause"]'
    NEXT_BUTTON = '[data-testid="control-button-skip-forward"]'
    PREV_BUTTON = '[data-testid="control-button-skip-back"]'
    SHUFFLE_BUTTON = '[data-testid="control-button-shuffle"]'
    REPEAT_BUTTON = '[data-testid="control-button-repeat"]'
    LIKE_BUTTON = '[data-testid="add-button"]'

    # --- Volume ---
    VOLUME_BAR = '[data-testid="volume-bar"]'
    VOLUME_SLIDER = '[data-testid="progress-bar"]'  # Inside volume bar

    # --- Progress ---
    PROGRESS_BAR = '[data-testid="playback-progressbar"]'

    # --- Navigation ---
    SEARCH_INPUT = '[data-testid="search-input"]'
    SEARCH_BUTTON = 'a[href="/search"]'
    HOME_BUTTON = 'a[href="/"]'
    LIBRARY_BUTTON = '[data-testid="your-library-button"]'

    # --- Sidebar (playlists) ---
    PLAYLIST_ITEM = '[data-testid="rootlist-item"]'
    PLAYLIST_NAME = '[data-testid="listrow-title"]'

    # --- Content Area ---
    TRACKLIST = '[data-testid="tracklist"]'
    TRACKLIST_ROW = '[data-testid="tracklist-row"]'
    TRACK_TITLE_IN_LIST = '[data-testid="internal-track-link"]'

    # --- Search Results ---
    SEARCH_RESULTS = '[data-testid="search-results"]'
    TOP_RESULT = '[data-testid="top-result-card"]'
    SEARCH_TRACK_LIST = '[data-testid="search-tracks-result"]'

    # --- Queue ---
    QUEUE_BUTTON = '[data-testid="control-button-queue"]'
    QUEUE_LIST = '[data-testid="queue-tracklist"]'

    # --- User ---
    USER_WIDGET = '[data-testid="user-widget-link"]'
    PREMIUM_BADGE = '[data-testid="premium-badge"]'


# =============================================================================
# URL Patterns
# =============================================================================

class SpotifyURLs:
    """Spotify Web Player URL patterns."""

    BASE = "https://open.spotify.com"

    @staticmethod
    def home() -> str:
        return SpotifyURLs.BASE

    @staticmethod
    def search(query: str) -> str:
        from urllib.parse import quote
        return f"{SpotifyURLs.BASE}/search/{quote(query)}"

    @staticmethod
    def search_tracks(query: str) -> str:
        from urllib.parse import quote
        return f"{SpotifyURLs.BASE}/search/{quote(query)}/tracks"

    @staticmethod
    def playlist(playlist_id: str) -> str:
        return f"{SpotifyURLs.BASE}/playlist/{playlist_id}"

    @staticmethod
    def album(album_id: str) -> str:
        return f"{SpotifyURLs.BASE}/album/{album_id}"

    @staticmethod
    def artist(artist_id: str) -> str:
        return f"{SpotifyURLs.BASE}/artist/{artist_id}"

    @staticmethod
    def track(track_id: str) -> str:
        return f"{SpotifyURLs.BASE}/track/{track_id}"

    @staticmethod
    def queue() -> str:
        return f"{SpotifyURLs.BASE}/queue"

    @staticmethod
    def collection_tracks() -> str:
        """Liked Songs."""
        return f"{SpotifyURLs.BASE}/collection/tracks"

    @staticmethod
    def collection_playlists() -> str:
        """Your playlists."""
        return f"{SpotifyURLs.BASE}/collection/playlists"
