"""LinkedIn sender using cc_linkedin CLI tool."""

import asyncio
import logging
import os
import sqlite3
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger("cc_director.dispatcher.linkedin")

CC_LINKEDIN_PATH = Path(r"C:\cc-tools\cc-linkedin.exe")
TEMP_MEDIA_DIR = Path(os.environ.get("TEMP", r"C:\temp")) / "cc_director_media"


@dataclass
class SendResult:
    """Result of a send operation."""

    success: bool
    message: str
    stdout: Optional[str] = None
    stderr: Optional[str] = None
    posted_url: Optional[str] = None


class LinkedInSender:
    """Send LinkedIn posts and comments via cc_linkedin CLI."""

    def __init__(self, db_path: Optional[Path] = None):
        """Initialize the LinkedIn sender.

        Args:
            db_path: Path to SQLite database (for extracting media BLOBs)
        """
        self.db_path = db_path
        TEMP_MEDIA_DIR.mkdir(parents=True, exist_ok=True)

    async def send(self, item: Dict[str, Any]) -> SendResult:
        """
        Send LinkedIn content using cc_linkedin CLI.

        Args:
            item: Content item dictionary with platform=linkedin

        Returns:
            SendResult indicating success/failure
        """
        item_type = item.get("type", "post").lower()

        if item_type == "comment":
            return await self._send_comment(item)
        elif item_type == "message":
            return await self._send_message(item)
        else:  # post, article
            return await self._send_post(item)

    async def _send_post(self, item: Dict[str, Any]) -> SendResult:
        """Create a LinkedIn post."""
        content = item.get("content", "")
        if not content:
            return SendResult(
                success=False,
                message="Missing post content"
            )

        # Build command
        cmd = [str(CC_LINKEDIN_PATH), "create", content]

        # Extract and attach media if present
        media = item.get("media", [])
        if media:
            image_path = await self._extract_first_image(item, media)
            if image_path:
                cmd.extend(["--image", str(image_path)])

        # Execute the command
        return await self._execute_command(cmd, "post")

    async def _send_comment(self, item: Dict[str, Any]) -> SendResult:
        """Post a comment on LinkedIn."""
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

        cmd = [str(CC_LINKEDIN_PATH), "comment", context_url, content]
        return await self._execute_command(cmd, "comment")

    async def _send_message(self, item: Dict[str, Any]) -> SendResult:
        """Send a LinkedIn direct message."""
        content = item.get("content", "")
        recipient = item.get("recipient", {})
        profile_url = recipient.get("profile_url", "")

        if not profile_url:
            return SendResult(
                success=False,
                message="Missing recipient profile_url for message"
            )

        if not content:
            return SendResult(
                success=False,
                message="Missing message content"
            )

        cmd = [str(CC_LINKEDIN_PATH), "message", profile_url, content]
        return await self._execute_command(cmd, "message")

    async def _extract_first_image(
        self,
        item: Dict[str, Any],
        media: List[Dict[str, Any]]
    ) -> Optional[Path]:
        """Extract first image from media list to temp file.

        Args:
            item: Content item (for ticket_number)
            media: List of media items

        Returns:
            Path to extracted temp file, or None
        """
        # Find first image
        first_image = None
        for m in media:
            if m.get("type") == "image":
                first_image = m
                break

        if not first_image:
            return None

        # Check if temp_path already exists
        temp_path = first_image.get("temp_path")
        if temp_path and Path(temp_path).exists():
            return Path(temp_path)

        # Extract from database
        if not self.db_path or not self.db_path.exists():
            logger.warning("No database path for media extraction")
            return None

        media_id = first_image.get("id")
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

            logger.info(f"Extracted media to {temp_path}")
            return temp_path

        except Exception as e:
            logger.error(f"Error extracting media: {e}")
            return None

    async def _execute_command(
        self,
        cmd: List[str],
        action_type: str
    ) -> SendResult:
        """Execute cc_linkedin command.

        Args:
            cmd: Command and arguments
            action_type: Type of action (post, comment, message)

        Returns:
            SendResult
        """
        try:
            logger.info(f"Sending LinkedIn {action_type}")
            logger.debug(f"Command: {' '.join(cmd[:3])}...")  # Don't log full content

            result = await asyncio.create_subprocess_exec(
                *cmd,
                stdout=asyncio.subprocess.PIPE,
                stderr=asyncio.subprocess.PIPE
            )
            stdout, stderr = await result.communicate()

            stdout_str = stdout.decode() if stdout else ""
            stderr_str = stderr.decode() if stderr else ""

            if result.returncode == 0:
                logger.info(f"LinkedIn {action_type} sent successfully")
                return SendResult(
                    success=True,
                    message=f"LinkedIn {action_type} sent",
                    stdout=stdout_str,
                    stderr=stderr_str
                )
            else:
                logger.error(f"LinkedIn {action_type} failed: {stderr_str}")
                logger.error(f"stdout: {stdout_str}")
                logger.error(f"Command was: {cmd[0]} {cmd[1]} [content...] {cmd[3:] if len(cmd) > 3 else ''}")
                return SendResult(
                    success=False,
                    message=f"Failed with exit code {result.returncode}",
                    stdout=stdout_str,
                    stderr=stderr_str
                )

        except FileNotFoundError:
            error_msg = f"cc_linkedin not found at {CC_LINKEDIN_PATH}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
        except Exception as e:
            error_msg = f"Error sending LinkedIn {action_type}: {e}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
