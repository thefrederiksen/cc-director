"""Core YouTube functionality using yt-dlp and youtube-transcript-api."""

import logging
import re
from dataclasses import dataclass, field
from typing import Any, Optional, List

logger = logging.getLogger(__name__)

import yt_dlp
from youtube_transcript_api import YouTubeTranscriptApi
from youtube_transcript_api._errors import (
    TranscriptsDisabled,
    NoTranscriptFound,
    VideoUnavailable,
    TranslationLanguageNotAvailable,
)


# Regex patterns for YouTube URL validation
YOUTUBE_URL_PATTERN = re.compile(
    r"^(https?://)?(www\.)?(youtube\.com|youtu\.be)/"
)
VIDEO_ID_PATTERN = re.compile(
    r"(?:v=|youtu\.be/)([a-zA-Z0-9_-]{11})"
)


class YouTubeError(Exception):
    """Base exception for YouTube operations."""

    pass


class InvalidURLError(YouTubeError):
    """Raised when URL is not a valid YouTube URL."""

    pass


class VideoNotFoundError(YouTubeError):
    """Raised when video cannot be accessed."""

    pass


class NoSubtitlesError(YouTubeError):
    """Raised when no subtitles are available."""

    pass


@dataclass
class Chapter:
    """Video chapter information."""

    start_time: float
    end_time: Optional[float]
    title: str

    @property
    def start_formatted(self) -> str:
        """Format start time as HH:MM:SS or MM:SS."""
        total_seconds = int(self.start_time)
        hours, remainder = divmod(total_seconds, 3600)
        minutes, secs = divmod(remainder, 60)
        if hours > 0:
            return f"{hours}:{minutes:02d}:{secs:02d}"
        return f"{minutes:02d}:{secs:02d}"


@dataclass
class VideoInfo:
    """YouTube video metadata."""

    id: str
    title: str
    channel: str
    duration_seconds: int
    thumbnail_url: Optional[str]
    has_captions: bool
    has_auto_captions: bool
    # Extended fields
    description: Optional[str] = None
    upload_date: Optional[str] = None
    view_count: Optional[int] = None
    like_count: Optional[int] = None
    comment_count: Optional[int] = None
    chapters: List[Chapter] = field(default_factory=list)
    # Additional metadata
    categories: List[str] = field(default_factory=list)
    tags: List[str] = field(default_factory=list)
    live_status: Optional[str] = None  # 'is_live', 'is_upcoming', 'was_live', 'not_live', 'post_live'
    channel_follower_count: Optional[int] = None
    age_limit: int = 0

    @property
    def duration_formatted(self) -> str:
        """Format duration as HH:MM:SS or MM:SS."""
        hours, remainder = divmod(self.duration_seconds, 3600)
        minutes, seconds = divmod(remainder, 60)
        if hours > 0:
            return f"{hours}:{minutes:02d}:{seconds:02d}"
        return f"{minutes}:{seconds:02d}"


def validate_url(url: str) -> bool:
    """
    Check if a URL is a valid YouTube URL format.

    Args:
        url: URL to validate

    Returns:
        True if valid YouTube URL format, False otherwise
    """
    if not url or not url.strip():
        return False
    return bool(YOUTUBE_URL_PATTERN.match(url.strip()))


def extract_video_id(url: str) -> Optional[str]:
    """
    Extract video ID from YouTube URL.

    Args:
        url: YouTube URL

    Returns:
        11-character video ID or None if not found
    """
    match = VIDEO_ID_PATTERN.search(url)
    return match.group(1) if match else None


def _format_upload_date(raw_date: Optional[str]) -> Optional[str]:
    """Format upload date from YYYYMMDD to YYYY-MM-DD."""
    if raw_date and len(raw_date) == 8:
        return f"{raw_date[:4]}-{raw_date[4:6]}-{raw_date[6:]}"
    return None


def _extract_chapters(info: dict) -> List[Chapter]:
    """Extract chapter list from yt-dlp info dict."""
    chapters = []
    for ch in info.get("chapters") or []:
        chapters.append(Chapter(
            start_time=ch.get("start_time", 0),
            end_time=ch.get("end_time"),
            title=ch.get("title", ""),
        ))
    return chapters


def _build_video_info(info: dict) -> VideoInfo:
    """Build VideoInfo dataclass from yt-dlp info dict."""
    subtitles = info.get("subtitles") or {}
    auto_captions = info.get("automatic_captions") or {}

    return VideoInfo(
        id=info.get("id", ""),
        title=info.get("title", ""),
        channel=info.get("channel", info.get("uploader", "")),
        duration_seconds=int(info.get("duration", 0)),
        thumbnail_url=info.get("thumbnail"),
        has_captions=bool(subtitles),
        has_auto_captions=bool(auto_captions),
        description=info.get("description"),
        upload_date=_format_upload_date(info.get("upload_date")),
        view_count=info.get("view_count"),
        like_count=info.get("like_count"),
        comment_count=info.get("comment_count"),
        chapters=_extract_chapters(info),
        categories=info.get("categories") or [],
        tags=info.get("tags") or [],
        live_status=info.get("live_status"),
        channel_follower_count=info.get("channel_follower_count"),
        age_limit=info.get("age_limit", 0),
    )


def get_video_info(url: str) -> VideoInfo:
    """
    Fetch video metadata from YouTube.

    Args:
        url: YouTube video URL

    Returns:
        VideoInfo dataclass with video metadata

    Raises:
        InvalidURLError: If URL is not a valid YouTube URL
        VideoNotFoundError: If video cannot be accessed
        YouTubeError: For other yt-dlp errors
    """
    if not validate_url(url):
        raise InvalidURLError(f"Invalid YouTube URL: {url}")

    opts = {
        "quiet": True,
        "no_warnings": True,
        "extract_flat": False,
    }

    try:
        with yt_dlp.YoutubeDL(opts) as ydl:
            info = ydl.extract_info(url, download=False)

            if info is None:
                raise VideoNotFoundError(f"Could not retrieve video info: {url}")

            return _build_video_info(info)

    except yt_dlp.utils.DownloadError as e:
        error_msg = str(e)
        if "Video unavailable" in error_msg or "Private video" in error_msg:
            raise VideoNotFoundError(f"Video not accessible: {url}")
        raise YouTubeError(f"Failed to get video info: {error_msg}")


def format_timestamp(seconds: float) -> str:
    """Format seconds as HH:MM:SS or MM:SS."""
    total_seconds = int(seconds)
    hours, remainder = divmod(total_seconds, 3600)
    minutes, secs = divmod(remainder, 60)
    if hours > 0:
        return f"{hours}:{minutes:02d}:{secs:02d}"
    return f"{minutes:02d}:{secs:02d}"


def _build_transcript_lines(snippets: List[Any], include_timestamps: bool) -> List[str]:
    """Build transcript lines from snippets, deduplicating text."""
    lines = []
    seen = set()
    for snippet in snippets:
        text = snippet.text.strip()
        if text and text not in seen:
            seen.add(text)
            if include_timestamps:
                timestamp = format_timestamp(snippet.start)
                lines.append(f"{timestamp}\t{text}")
            else:
                lines.append(text)
    return lines


def _find_best_transcript(
    transcript_list: Any,
    language: str,
    prefer_manual: bool,
) -> Optional[Any]:
    """
    Find the best available transcript from a transcript list.

    Args:
        transcript_list: TranscriptList from youtube-transcript-api
        language: Target language code
        prefer_manual: If True, prefer manual captions over auto-generated

    Returns:
        Transcript object or None if not found
    """
    transcript = None

    if prefer_manual:
        try:
            transcript = transcript_list.find_manually_created_transcript([language])
        except NoTranscriptFound:
            pass

    if transcript is None:
        try:
            transcript = transcript_list.find_generated_transcript([language])
        except NoTranscriptFound:
            pass

    # Try any transcript and translate if needed
    if transcript is None:
        for t in transcript_list:
            transcript = t
            break
        if transcript and transcript.language_code != language:
            try:
                transcript = transcript.translate(language)
            except (NoTranscriptFound, TranslationLanguageNotAvailable):
                # Translation failed - keep original language transcript
                pass

    return transcript


def download_transcript(
    url: str,
    language: str = "en",
    prefer_manual: bool = True,
    include_timestamps: bool = True,
) -> str:
    """
    Download transcript from YouTube video using youtube-transcript-api.

    Args:
        url: YouTube video URL
        language: Subtitle language code (default: "en")
        prefer_manual: If True, prefer manual captions over auto-generated
        include_timestamps: If True, prefix each line with timestamp (default: True)

    Returns:
        Transcript text (with timestamps by default)

    Raises:
        InvalidURLError: If URL is not a valid YouTube URL
        VideoNotFoundError: If video cannot be accessed
        NoSubtitlesError: If no subtitles are available
        YouTubeError: For other errors
    """
    if not validate_url(url):
        raise InvalidURLError(f"Invalid YouTube URL: {url}")

    video_id = extract_video_id(url)
    if not video_id:
        raise InvalidURLError(f"Could not extract video ID from: {url}")

    try:
        ytt_api = YouTubeTranscriptApi()
        transcript_list = ytt_api.list(video_id)
        transcript = _find_best_transcript(transcript_list, language, prefer_manual)

        if transcript is None:
            raise NoSubtitlesError(
                f"No subtitles available for this video in language '{language}'. "
                "The video may not have captions."
            )

        fetched = transcript.fetch()
        lines = _build_transcript_lines(fetched.snippets, include_timestamps)

        if not lines:
            raise NoSubtitlesError("Transcript was empty after processing.")

        return "\n".join(lines)

    except TranscriptsDisabled:
        raise NoSubtitlesError(
            "Transcripts are disabled for this video."
        )
    except VideoUnavailable:
        raise VideoNotFoundError(f"Video not accessible: {url}")
    except NoTranscriptFound:
        raise NoSubtitlesError(
            f"No subtitles available for this video in language '{language}'."
        )
    except TranslationLanguageNotAvailable:
        raise NoSubtitlesError(
            f"Translation not available for language '{language}'."
        )
    except (NoSubtitlesError, VideoNotFoundError, InvalidURLError):
        raise
    # youtube_transcript_api can raise: KeyError (missing data), ValueError (parsing),
    # TypeError (type mismatches), OSError (network), RuntimeError (internal errors)
    except (KeyError, ValueError, TypeError, OSError, RuntimeError) as e:
        logger.debug("Unexpected error downloading transcript: %s", e)
        raise YouTubeError(f"Failed to download transcript: {e}") from e


@dataclass
class LanguageInfo:
    """Transcript language information."""

    code: str
    name: str
    is_generated: bool
    is_translatable: bool


def list_languages(url: str) -> List[LanguageInfo]:
    """
    List available transcript languages for a video.

    Args:
        url: YouTube video URL

    Returns:
        List of LanguageInfo objects

    Raises:
        InvalidURLError: If URL is not a valid YouTube URL
        VideoNotFoundError: If video cannot be accessed
        NoSubtitlesError: If no subtitles are available
        YouTubeError: For other errors
    """
    if not validate_url(url):
        raise InvalidURLError(f"Invalid YouTube URL: {url}")

    video_id = extract_video_id(url)
    if not video_id:
        raise InvalidURLError(f"Could not extract video ID from: {url}")

    try:
        ytt_api = YouTubeTranscriptApi()
        transcript_list = ytt_api.list(video_id)

        languages = []
        for t in transcript_list:
            languages.append(LanguageInfo(
                code=t.language_code,
                name=t.language,
                is_generated=t.is_generated,
                is_translatable=t.is_translatable,
            ))

        if not languages:
            raise NoSubtitlesError("No subtitles available for this video.")

        return languages

    except TranscriptsDisabled:
        raise NoSubtitlesError("Transcripts are disabled for this video.")
    except VideoUnavailable:
        raise VideoNotFoundError(f"Video not accessible: {url}")
    except (NoSubtitlesError, VideoNotFoundError, InvalidURLError):
        raise
    # youtube_transcript_api can raise: KeyError (missing data), ValueError (parsing),
    # TypeError (type mismatches), OSError (network), RuntimeError (internal errors)
    except (KeyError, ValueError, TypeError, OSError, RuntimeError) as e:
        logger.debug("Unexpected error listing languages: %s", e)
        raise YouTubeError(f"Failed to list languages: {e}") from e


def download_transcript_formatted(
    url: str,
    language: str = "en",
    format: str = "txt",
    prefer_manual: bool = True,
) -> str:
    """
    Download transcript in specified format (txt, srt, vtt).

    Args:
        url: YouTube video URL
        language: Subtitle language code (default: "en")
        format: Output format - "txt", "srt", or "vtt" (default: "txt")
        prefer_manual: If True, prefer manual captions over auto-generated

    Returns:
        Transcript text in the specified format

    Raises:
        InvalidURLError: If URL is not a valid YouTube URL
        VideoNotFoundError: If video cannot be accessed
        NoSubtitlesError: If no subtitles are available
        YouTubeError: For other errors
    """
    if format not in ("txt", "srt", "vtt"):
        raise YouTubeError(f"Unknown format: {format}. Use 'txt', 'srt', or 'vtt'.")

    if format == "txt":
        return download_transcript(url, language, prefer_manual, include_timestamps=True)

    # Import formatters for SRT/VTT
    from youtube_transcript_api.formatters import SRTFormatter, WebVTTFormatter

    if not validate_url(url):
        raise InvalidURLError(f"Invalid YouTube URL: {url}")

    video_id = extract_video_id(url)
    if not video_id:
        raise InvalidURLError(f"Could not extract video ID from: {url}")

    try:
        ytt_api = YouTubeTranscriptApi()
        transcript_list = ytt_api.list(video_id)
        transcript = _find_best_transcript(transcript_list, language, prefer_manual)

        if transcript is None:
            raise NoSubtitlesError(
                f"No subtitles available for this video in language '{language}'."
            )

        fetched = transcript.fetch()
        formatter = SRTFormatter() if format == "srt" else WebVTTFormatter()
        return formatter.format_transcript(fetched.snippets)

    except TranscriptsDisabled:
        raise NoSubtitlesError("Transcripts are disabled for this video.")
    except VideoUnavailable:
        raise VideoNotFoundError(f"Video not accessible: {url}")
    except NoTranscriptFound:
        raise NoSubtitlesError(
            f"No subtitles available for this video in language '{language}'."
        )
    except TranslationLanguageNotAvailable:
        raise NoSubtitlesError(
            f"Translation not available for language '{language}'."
        )
    except (NoSubtitlesError, VideoNotFoundError, InvalidURLError, YouTubeError):
        raise
    # youtube_transcript_api can raise: KeyError (missing data), ValueError (parsing),
    # TypeError (type mismatches), OSError (network), RuntimeError (internal errors)
    except (KeyError, ValueError, TypeError, OSError, RuntimeError) as e:
        logger.debug("Unexpected error downloading formatted transcript: %s", e)
        raise YouTubeError(f"Failed to download transcript: {e}") from e
