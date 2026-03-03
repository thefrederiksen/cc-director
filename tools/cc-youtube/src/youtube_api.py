"""YouTube Data API v3 wrapper for common operations."""

import logging
import re
from pathlib import Path
from typing import Optional, List, Dict, Any

from googleapiclient.discovery import build
from googleapiclient.errors import HttpError
from googleapiclient.http import MediaFileUpload
from google.oauth2.credentials import Credentials

logger = logging.getLogger(__name__)


def extract_video_id(url_or_id: str) -> str:
    """Extract video ID from a YouTube URL or return the ID if already bare.

    Supported formats:
        - https://www.youtube.com/watch?v=XXXX
        - https://youtu.be/XXXX
        - https://www.youtube.com/embed/XXXX
        - https://www.youtube.com/v/XXXX
        - Just the ID itself (11 characters)

    Args:
        url_or_id: A YouTube URL or video ID.

    Returns:
        The extracted video ID.

    Raises:
        ValueError: If the video ID cannot be extracted.
    """
    if not url_or_id:
        raise ValueError("Empty video URL or ID")

    # Pattern for youtube.com/watch?v=ID
    match = re.search(r'(?:v=|v/|embed/|youtu\.be/)([a-zA-Z0-9_-]{11})', url_or_id)
    if match:
        return match.group(1)

    # If it looks like a bare ID (11 alphanumeric chars plus - and _)
    if re.match(r'^[a-zA-Z0-9_-]{11}$', url_or_id):
        return url_or_id

    raise ValueError(
        f"Cannot extract video ID from: {url_or_id}\n\n"
        "Expected formats:\n"
        "  - https://www.youtube.com/watch?v=VIDEO_ID\n"
        "  - https://youtu.be/VIDEO_ID\n"
        "  - VIDEO_ID (11 characters)"
    )


class YouTubeAPI:
    """YouTube Data API v3 client wrapper."""

    def __init__(self, credentials: Credentials):
        """Initialize YouTube client with credentials.

        Args:
            credentials: Valid OAuth credentials for YouTube API.
        """
        self.service = build("youtube", "v3", credentials=credentials)

    def get_channel_info(self) -> Dict[str, Any]:
        """Get the authenticated user's channel information.

        Returns:
            Dict with channel id, title, description, subscriber count, video count, etc.
        """
        response = self.service.channels().list(
            part="snippet,statistics,contentDetails",
            mine=True,
        ).execute()

        items = response.get("items", [])
        if not items:
            return {"error": "No channel found for authenticated user"}

        channel = items[0]
        snippet = channel.get("snippet", {})
        stats = channel.get("statistics", {})

        return {
            "id": channel.get("id"),
            "title": snippet.get("title"),
            "description": snippet.get("description"),
            "custom_url": snippet.get("customUrl"),
            "published_at": snippet.get("publishedAt"),
            "subscriber_count": stats.get("subscriberCount", "0"),
            "video_count": stats.get("videoCount", "0"),
            "view_count": stats.get("viewCount", "0"),
        }

    def upload(
        self,
        file_path: str,
        title: str,
        description: str,
        tags: Optional[List[str]] = None,
        category: str = "22",
        privacy: str = "private",
        thumbnail: Optional[str] = None,
    ) -> Dict[str, Any]:
        """Upload a video to YouTube.

        Args:
            file_path: Path to the video file.
            title: Video title.
            description: Video description.
            tags: Optional list of tags.
            category: YouTube category ID (default "22" = People & Blogs).
            privacy: Privacy status: "private", "unlisted", or "public".
            thumbnail: Optional path to thumbnail image.

        Returns:
            Dict with video id and url.

        Raises:
            FileNotFoundError: If the video file does not exist.
            HttpError: If the YouTube API returns an error.
        """
        video_path = Path(file_path)
        if not video_path.exists():
            raise FileNotFoundError(f"Video file not found: {file_path}")

        body = {
            "snippet": {
                "title": title,
                "description": description,
                "categoryId": category,
            },
            "status": {
                "privacyStatus": privacy,
            },
        }

        if tags:
            body["snippet"]["tags"] = tags

        media = MediaFileUpload(
            str(video_path),
            resumable=True,
            chunksize=10 * 1024 * 1024,  # 10 MB chunks
        )

        request = self.service.videos().insert(
            part="snippet,status",
            body=body,
            media_body=media,
        )

        # Upload with progress reporting
        from rich.progress import Progress, BarColumn, TextColumn, TimeRemainingColumn

        response = None
        with Progress(
            TextColumn("[bold blue]Uploading"),
            BarColumn(),
            TextColumn("{task.percentage:>3.0f}%"),
            TimeRemainingColumn(),
        ) as progress:
            task = progress.add_task("upload", total=100)
            while response is None:
                status, response = request.next_chunk()
                if status:
                    progress.update(task, completed=int(status.progress() * 100))
            progress.update(task, completed=100)

        video_id = response.get("id")

        # Set thumbnail if provided
        if thumbnail:
            thumb_path = Path(thumbnail)
            if not thumb_path.exists():
                logger.warning("Thumbnail file not found: %s", thumbnail)
            else:
                self.service.thumbnails().set(
                    videoId=video_id,
                    media_body=MediaFileUpload(str(thumb_path)),
                ).execute()

        return {
            "id": video_id,
            "url": f"https://youtu.be/{video_id}",
            "title": title,
            "privacy": privacy,
        }

    def comment(self, video_id: str, text: str) -> Dict[str, Any]:
        """Post a top-level comment on a video.

        Args:
            video_id: The video ID to comment on.
            text: Comment text.

        Returns:
            Dict with comment id and text.
        """
        body = {
            "snippet": {
                "videoId": video_id,
                "topLevelComment": {
                    "snippet": {
                        "textOriginal": text,
                    }
                }
            }
        }

        response = self.service.commentThreads().insert(
            part="snippet",
            body=body,
        ).execute()

        comment_id = response.get("id")
        return {
            "id": comment_id,
            "video_id": video_id,
            "text": text,
        }

    def reply(self, parent_comment_id: str, text: str) -> Dict[str, Any]:
        """Reply to an existing comment.

        Args:
            parent_comment_id: The parent comment thread ID to reply to.
            text: Reply text.

        Returns:
            Dict with reply id and text.
        """
        body = {
            "snippet": {
                "parentId": parent_comment_id,
                "textOriginal": text,
            }
        }

        response = self.service.comments().insert(
            part="snippet",
            body=body,
        ).execute()

        reply_id = response.get("id")
        return {
            "id": reply_id,
            "parent_id": parent_comment_id,
            "text": text,
        }

    def delete_video(self, video_id: str) -> bool:
        """Delete a video.

        Args:
            video_id: The video ID to delete.

        Returns:
            True if the video was deleted successfully.
        """
        self.service.videos().delete(id=video_id).execute()
        return True

    def list_videos(self, count: int = 10) -> List[Dict[str, Any]]:
        """List the authenticated user's uploaded videos.

        Args:
            count: Maximum number of videos to return.

        Returns:
            List of video dicts with id, title, published_at, view_count, etc.
        """
        response = self.service.search().list(
            part="snippet",
            forMine=True,
            type="video",
            maxResults=min(count, 50),
            order="date",
        ).execute()

        videos = []
        video_ids = []
        for item in response.get("items", []):
            video_ids.append(item["id"]["videoId"])

        # Fetch statistics for all videos in one call
        stats_map = {}
        if video_ids:
            stats_response = self.service.videos().list(
                part="statistics,status",
                id=",".join(video_ids),
            ).execute()
            for item in stats_response.get("items", []):
                stats_map[item["id"]] = {
                    "statistics": item.get("statistics", {}),
                    "status": item.get("status", {}),
                }

        for item in response.get("items", []):
            snippet = item.get("snippet", {})
            vid = item["id"]["videoId"]
            stats = stats_map.get(vid, {})
            statistics = stats.get("statistics", {})
            status = stats.get("status", {})

            videos.append({
                "id": vid,
                "title": snippet.get("title"),
                "description": snippet.get("description", ""),
                "published_at": snippet.get("publishedAt"),
                "url": f"https://youtu.be/{vid}",
                "view_count": statistics.get("viewCount", "0"),
                "like_count": statistics.get("likeCount", "0"),
                "comment_count": statistics.get("commentCount", "0"),
                "privacy": status.get("privacyStatus", "unknown"),
            })

        return videos

    def list_comments(self, video_id: str, count: int = 20) -> List[Dict[str, Any]]:
        """List comments on a video.

        Args:
            video_id: The video ID to list comments for.
            count: Maximum number of comment threads to return.

        Returns:
            List of comment dicts with id, author, text, like_count, etc.
        """
        response = self.service.commentThreads().list(
            part="snippet",
            videoId=video_id,
            maxResults=min(count, 100),
            order="relevance",
        ).execute()

        comments = []
        for item in response.get("items", []):
            top = item.get("snippet", {}).get("topLevelComment", {})
            snippet = top.get("snippet", {})
            comments.append({
                "id": item.get("id"),
                "author": snippet.get("authorDisplayName"),
                "text": snippet.get("textDisplay", ""),
                "like_count": snippet.get("likeCount", 0),
                "published_at": snippet.get("publishedAt"),
                "reply_count": item.get("snippet", {}).get("totalReplyCount", 0),
            })

        return comments
