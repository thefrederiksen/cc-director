# cc-spotify

Spotify CLI via browser automation. Controls the Spotify Web Player (open.spotify.com) through a cc-browser workspace. No Spotify API key needed.

## Setup

1. Configure a workspace (any existing cc-browser workspace where you're logged into Spotify):

```bash
cc-spotify config --workspace edge-personal
```

2. Start the browser (if not already running):

```bash
cc-browser start --workspace edge-personal
```

3. Navigate to open.spotify.com in the browser and log in (one-time, session persists).

4. Verify connection:

```bash
cc-spotify status
```

## Commands

### Playback

```bash
cc-spotify now                          # Current track, artist, progress
cc-spotify play                         # Resume playback
cc-spotify pause                        # Pause playback
cc-spotify next                         # Skip to next track
cc-spotify prev                         # Previous track
```

### Controls

```bash
cc-spotify shuffle --on                 # Enable shuffle
cc-spotify shuffle --off                # Disable shuffle
cc-spotify repeat off                   # Disable repeat
cc-spotify repeat context               # Repeat playlist/album
cc-spotify repeat track                 # Repeat current track
cc-spotify volume 75                    # Set volume (0-100)
cc-spotify like                         # Heart current track
```

### Browse

```bash
cc-spotify search "Miles Davis"         # Search tracks/artists/albums
cc-spotify playlists                    # List sidebar playlists
cc-spotify playlist "Chill Vibes"       # Play a playlist by name
cc-spotify queue                        # Show playback queue
cc-spotify goto URL                     # Navigate to Spotify URL
```

### Vault Integration

```bash
cc-spotify recommend                    # Suggestions from vault preferences
cc-spotify recommend --mood "chill"     # Mood-filtered suggestions
```

### Configuration

```bash
cc-spotify config --workspace NAME      # Set default workspace
cc-spotify config --show                # Show current config
```

## Global Options

| Option | Description |
|--------|-------------|
| `--workspace, -w` | Override workspace per-call |
| `--format, -f` | Output format: text, json |
| `--verbose, -v` | Dump raw snapshots for debugging |

## How It Works

cc-spotify uses three interaction methods:

| Method | When | Why |
|--------|------|-----|
| Keyboard shortcuts | play/pause, next, prev | Most reliable, survives UI redesigns |
| JavaScript evaluation | Reading track info, playlists | Direct DOM access |
| Snapshot + click | Shuffle, repeat, like buttons | For buttons without keyboard shortcuts |

## Requirements

- cc-browser daemon running with a workspace
- Spotify Web Player open and logged in (Premium recommended for full control)
- Python 3.10+

## Troubleshooting

**"No workspace configured"** - Run `cc-spotify config --workspace <name>`

**"Cannot connect to daemon"** - Start cc-browser: `cc-browser start --workspace <name>`

**"No now-playing widget found"** - Navigate to open.spotify.com and start playing a track

**Volume not working** - Spotify's React state may fight the JS slider change. Click in the player first.

**Selectors broken after Spotify update** - Use `--verbose` to dump snapshots and update `selectors.py`

## Build

```powershell
.\build.ps1
```

Produces `dist\cc-spotify.exe` via PyInstaller.
