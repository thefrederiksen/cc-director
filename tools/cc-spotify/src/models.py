"""Data models for Spotify playback state."""

from pydantic import BaseModel
from typing import Optional


class Track(BaseModel):
    """Currently playing track info."""
    name: str = ""
    artist: str = ""
    album: str = ""
    position: str = ""
    duration: str = ""
    is_playing: bool = False
    is_liked: bool = False


class PlaybackState(BaseModel):
    """Full playback state."""
    track: Track = Track()
    shuffle: bool = False
    repeat: str = "off"       # off, context, track
    volume: int = -1          # 0-100, -1 = unknown


class PlaylistItem(BaseModel):
    """Playlist from sidebar."""
    name: str = ""
    ref: str = ""             # Element ref for clicking


class SearchResult(BaseModel):
    """Single search result."""
    name: str = ""
    artist: str = ""
    type: str = ""            # track, artist, album, playlist
    ref: str = ""


class QueueItem(BaseModel):
    """Track in the queue."""
    name: str = ""
    artist: str = ""
    position: Optional[int] = None
