"""YouTube sender using cc-youtube CLI tool."""

import asyncio
import logging
import os
import sqlite3
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger("cc_director.dispatcher.youtube")


def _bin_dir() -> Path:
    """Resolve the cc-director bin directory."""
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "cc-director" / "bin"
    return Path.home() / ".cc-director" / "bin"


CC_YOUTUBE_PATH = _bin_dir() / "cc-youtube.exe"
TEMP_MEDIA_DIR = Path(os.environ.get("TEMP", r"C:\temp")) / "cc_director_media"


@dataclass
class SendResult:
    """Result of a send operation."""

    success: bool
    message: str
    stdout: Optional[str] = None
    stderr: Optional[str] = None
    posted_url: Optional[str] = None


class YouTubeSender:
    """Send YouTube uploads, comments, and replies via cc-youtube CLI."""

    def __init__(self, db_path: Optional[Path] = None):
        """Initialize the YouTube sender.

        Args:
            db_path: Path to SQLite database (for extracting media BLOBs)
        """
        self.db_path = db_path
        TEMP_MEDIA_DIR.mkdir(parents=True, exist_ok=True)

    async def send(self, item: Dict[str, Any]) -> SendResult:
        """Send YouTube content using cc-youtube CLI.

        Args:
            item: Content item dictionary with platform=youtube

        Returns:
            SendResult indicating success/failure
        """
        item_type = item.get("type", "post").lower()

        if item_type == "comment":
            return await self._send_comment(item)
        elif item_type == "reply":
            return await self._send_reply(item)
        else:  # post = video upload
            return await self._send_upload(item)

    async def _send_upload(self, item: Dict[str, Any]) -> SendResult:
        """Upload a video to YouTube."""
        youtube_specific = item.get("youtube_specific", {}) or {}

        title = youtube_specific.get("title", "")
        description = youtube_specific.get("description", "") or item.get("content", "")
        video_file_path = youtube_specific.get("video_file_path", "")
        tags = youtube_specific.get("tags", [])
        category = youtube_specific.get("category", "22")
        privacy = youtube_specific.get("privacy_status", "private")
        thumbnail_path = youtube_specific.get("thumbnail_path", "")

        if not title:
            return SendResult(
                success=False,
                message="Missing title in youtube_specific"
            )

        # Try to get video file from media if not in youtube_specific
        if not video_file_path:
            video_file_path = await self._extract_video_file(item)

        if not video_file_path:
            return SendResult(
                success=False,
                message="Missing video_file_path in youtube_specific and no video in media"
            )

        if not Path(video_file_path).exists():
            return SendResult(
                success=False,
                message=f"Video file not found: {video_file_path}"
            )

        # Build command
        cmd = [
            str(CC_YOUTUBE_PATH), "upload", video_file_path,
            "--title", title,
            "--description", description or "",
            "--privacy", privacy,
            "--category", str(category),
        ]

        if tags:
            tag_str = ",".join(tags) if isinstance(tags, list) else str(tags)
            cmd.extend(["--tags", tag_str])

        if thumbnail_path and Path(thumbnail_path).exists():
            cmd.extend(["--thumbnail", thumbnail_path])

        return await self._execute_command(cmd, "upload")

    async def _send_comment(self, item: Dict[str, Any]) -> SendResult:
        """Comment on a YouTube video."""
        content = item.get("content", "")
        context_url = item.get("context_url", "")

        if not context_url:
            return SendResult(
                success=False,
                message="Missing context_url for comment"
            )

        if not content:
            return SendResult(
                success=False,
                message="Missing comment content"
            )

        cmd = [str(CC_YOUTUBE_PATH), "comment", context_url, content]
        return await self._execute_command(cmd, "comment")

    async def _send_reply(self, item: Dict[str, Any]) -> SendResult:
        """Reply to a YouTube comment."""
        content = item.get("content", "")
        youtube_specific = item.get("youtube_specific", {}) or {}
        comment_id = youtube_specific.get("parent_comment_id", "") or item.get("context_url", "")

        if not comment_id:
            return SendResult(
                success=False,
                message="Missing parent_comment_id in youtube_specific or context_url"
            )

        if not content:
            return SendResult(
                success=False,
                message="Missing reply content"
            )

        cmd = [str(CC_YOUTUBE_PATH), "reply", comment_id, content]
        return await self._execute_command(cmd, "reply")

    async def _extract_video_file(self, item: Dict[str, Any]) -> Optional[str]:
        """Extract video file from media list.

        Args:
            item: Content item with media list

        Returns:
            Path to video file, or None
        """
        media = item.get("media", [])
        first_video = None
        for m in media:
            if m.get("type") == "video":
                first_video = m
                break

        if not first_video:
            return None

        # Check if temp_path already exists
        temp_path = first_video.get("temp_path")
        if temp_path and Path(temp_path).exists():
            return temp_path

        # Extract from database
        if not self.db_path or not self.db_path.exists():
            logger.warning("No database path for media extraction")
            return None

        media_id = first_video.get("id")
        if not media_id:
            logger.warning("No media ID for extraction")
            return None

        try:
            conn = sqlite3.connect(self.db_path)
            cursor = conn.execute(
                "SELECT filename, data FROM media WHERE id = ?",
                (media_id,)
            )
            row = cursor.fetchone()
            conn.close()

            if not row:
                logger.warning(f"Media ID {media_id} not found in database")
                return None

            filename, data = row
            temp_path = TEMP_MEDIA_DIR / f"{media_id}_{filename}"
            temp_path.write_bytes(data)

            logger.info(f"Extracted video to {temp_path}")
            return str(temp_path)

        except Exception as e:
            logger.error(f"Error extracting video media: {e}")
            return None

    async def _execute_command(
        self,
        cmd: List[str],
        action_type: str
    ) -> SendResult:
        """Execute cc-youtube command.

        Args:
            cmd: Command and arguments
            action_type: Type of action (upload, comment, reply)

        Returns:
            SendResult
        """
        try:
            logger.info(f"Sending YouTube {action_type}")
            logger.debug(f"Command: {' '.join(cmd[:3])}...")

            result = await asyncio.create_subprocess_exec(
                *cmd,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE
            )
            stdout, stderr = await result.communicate()

            stdout_str = stdout.decode() if stdout else ""
            stderr_str = stderr.decode() if stderr else ""

            if result.returncode == 0:
                logger.info(f"YouTube {action_type} sent successfully")
                return SendResult(
                    success=True,
                    message=f"YouTube {action_type} sent",
                    stdout=stdout_str,
                    stderr=stderr_str
                )
            else:
                logger.error(f"YouTube {action_type} failed: {stderr_str}")
                logger.error(f"stdout: {stdout_str}")
                return SendResult(
                    success=False,
                    message=f"Failed with exit code {result.returncode}",
                    stdout=stdout_str,
                    stderr=stderr_str
                )

        except FileNotFoundError:
            error_msg = f"cc-youtube not found at {CC_YOUTUBE_PATH}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
        except Exception as e:
            error_msg = f"Error sending YouTube {action_type}: {e}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
