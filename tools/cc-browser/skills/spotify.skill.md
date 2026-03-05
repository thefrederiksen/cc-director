---
name: spotify
version: 2026.03.05
description: Navigate Spotify Web Player
site: open.spotify.com
flavor: managed
forked_from: null
---

# Spotify Web Player Navigation Skill

## Authentication Check

Logged-in state is indicated by:
- User widget: `[data-testid="user-widget-link"]`
- Premium badge (if applicable): `[data-testid="premium-badge"]`

## URL Patterns

| Page | URL |
|------|-----|
| Home | `https://open.spotify.com` |
| Search | `https://open.spotify.com/search/{query}` |
| Search tracks | `https://open.spotify.com/search/{query}/tracks` |
| Playlist | `https://open.spotify.com/playlist/{id}` |
| Album | `https://open.spotify.com/album/{id}` |
| Artist | `https://open.spotify.com/artist/{id}` |
| Track | `https://open.spotify.com/track/{id}` |
| Queue | `https://open.spotify.com/queue` |
| Liked Songs | `https://open.spotify.com/collection/tracks` |
| Your Playlists | `https://open.spotify.com/collection/playlists` |

## Keyboard Shortcuts

Keyboard shortcuts are the most reliable interaction method -- they survive UI redesigns.

| Action | Shortcut |
|--------|----------|
| Play/Pause | `Space` |
| Next track | `Shift+ArrowRight` |
| Previous track | `Shift+ArrowLeft` |
| Shuffle toggle | `s` |
| Repeat cycle | `r` |
| Like/Save | `Alt+Shift+b` |

Note: Volume Up/Down (`ArrowUp`/`ArrowDown`) only work when volume slider is focused.

## Key Elements

Spotify uses `data-testid` attributes extensively. These are semi-stable.

### Now Playing Bar (bottom)
| Element | Selector |
|---------|----------|
| Now playing bar | `[data-testid="now-playing-bar"]` |
| Now playing widget | `[data-testid="now-playing-widget"]` |
| Track name | `[data-testid="context-item-link"]` |
| Artist name | `[data-testid="context-item-info-subtitles"]` |
| Album art | `[data-testid="cover-art-image"]` |
| Position time | `[data-testid="playback-position"]` |
| Duration time | `[data-testid="playback-duration"]` |

### Playback Controls
| Element | Selector |
|---------|----------|
| Play/Pause | `[data-testid="control-button-playpause"]` |
| Next | `[data-testid="control-button-skip-forward"]` |
| Previous | `[data-testid="control-button-skip-back"]` |
| Shuffle | `[data-testid="control-button-shuffle"]` |
| Repeat | `[data-testid="control-button-repeat"]` |
| Like/Save | `[data-testid="add-button"]` |
| Queue | `[data-testid="control-button-queue"]` |

### Volume
| Element | Selector |
|---------|----------|
| Volume bar | `[data-testid="volume-bar"]` |
| Volume slider | `[data-testid="progress-bar"]` (inside volume bar) |

### Progress
| Element | Selector |
|---------|----------|
| Progress bar | `[data-testid="playback-progressbar"]` |

### Navigation
| Element | Selector |
|---------|----------|
| Search input | `[data-testid="search-input"]` |
| Search button | `a[href="/search"]` |
| Home button | `a[href="/"]` |
| Library button | `[data-testid="your-library-button"]` |

### Sidebar (Playlists)
| Element | Selector |
|---------|----------|
| Playlist item | `[data-testid="rootlist-item"]` |
| Playlist name | `[data-testid="listrow-title"]` |

### Track Lists
| Element | Selector |
|---------|----------|
| Track list | `[data-testid="tracklist"]` |
| Track row | `[data-testid="tracklist-row"]` |
| Track title link | `[data-testid="internal-track-link"]` |

### Search Results
| Element | Selector |
|---------|----------|
| Results container | `[data-testid="search-results"]` |
| Top result card | `[data-testid="top-result-card"]` |
| Tracks result | `[data-testid="search-tracks-result"]` |

### Queue
| Element | Selector |
|---------|----------|
| Queue list | `[data-testid="queue-tracklist"]` |

## Workflows

### Check Now Playing
1. Look at the now playing bar at the bottom
2. Track name: `[data-testid="context-item-link"]`
3. Artist: `[data-testid="context-item-info-subtitles"]`
4. Position/duration: `[data-testid="playback-position"]` / `[data-testid="playback-duration"]`

### Search for Music
1. Navigate to `https://open.spotify.com/search/{query}`
2. Wait for `[data-testid="search-results"]`
3. Top result in `[data-testid="top-result-card"]`
4. Track results in `[data-testid="search-tracks-result"]`

### Play a Track from Search
1. Search for the track
2. Find the track row in results
3. Double-click the track row, or click the play button on hover

### Browse a Playlist
1. Navigate to `https://open.spotify.com/playlist/{id}`
2. Wait for `[data-testid="tracklist"]`
3. Tracks are in `[data-testid="tracklist-row"]` elements

## Gotchas

- **Keyboard shortcuts are most reliable**: Prefer keyboard shortcuts over clicking
  UI elements. They survive UI redesigns.
- **data-testid stability**: Selectors using `data-testid` are semi-stable but can
  change during major redesigns. Use `cc-browser snapshot --interactive` to verify.
- **Premium vs Free**: Some UI elements differ between Premium and Free accounts.
  Free accounts may show ads and have limited skip functionality.
- **Web Player vs Desktop**: This skill is for the web player only. Desktop app
  has a different interface.
- **Dynamic content**: Track lists load lazily. Long playlists require scrolling
  to load more tracks.
- **Ads (Free tier)**: Ad breaks interrupt playback and change the now-playing
  bar content temporarily.
