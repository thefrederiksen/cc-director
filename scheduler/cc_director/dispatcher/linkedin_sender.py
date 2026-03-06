"""LinkedIn sender using cc-browser CLI with LinkedIn connection."""

import asyncio
import logging
import os
import sqlite3
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger("cc_director.dispatcher.linkedin")

def _bin_dir() -> Path:
    """Resolve the cc-director bin directory."""
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "cc-director" / "bin"
    return Path.home() / ".cc-director" / "bin"


CC_BROWSER_PATH = _bin_dir() / "cc-browser.exe"
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
    """Send LinkedIn posts, comments, and messages via cc-browser CLI."""

    def __init__(self, db_path: Optional[Path] = None, connection: str = "linkedin"):
        """Initialize the LinkedIn sender.

        Args:
            db_path: Path to SQLite database (for extracting media BLOBs)
            connection: cc-browser connection name for LinkedIn
        """
        self.db_path = db_path
        self.connection = connection
        TEMP_MEDIA_DIR.mkdir(parents=True, exist_ok=True)

    async def send(self, item: Dict[str, Any]) -> SendResult:
        """
        Send LinkedIn content using cc-browser CLI.

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

        # Navigate to LinkedIn feed and create post via browser automation
        cmd = [
            str(CC_BROWSER_PATH),
            "--connection", self.connection,
            "navigate", "--url", "https://www.linkedin.com/feed/"
        ]

        # For posts, we need multi-step browser automation.
        # This is a placeholder -- post creation requires LLM agent orchestration
        # using the LinkedIn navigation skill's "Create a Post" workflow.
        logger.warning("LinkedIn post dispatch requires LLM agent orchestration via cc-browser")
        return SendResult(
            success=False,
            message="LinkedIn post dispatch not yet implemented via cc-browser. Use LLM agent with LinkedIn navigation skill."
        )

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

        # Comment creation requires multi-step browser automation.
        logger.warning("LinkedIn comment dispatch requires LLM agent orchestration via cc-browser")
        return SendResult(
            success=False,
            message="LinkedIn comment dispatch not yet implemented via cc-browser. Use LLM agent with LinkedIn navigation skill."
        )

    async def _send_message(self, item: Dict[str, Any]) -> SendResult:
        """Send a LinkedIn direct message."""
        content = item.get("content", "")
        recipient = item.get("recipient", {})
        profile_url = recipient.get("profile_url", "")

        if not profile_url:
            # Try destination_url as fallback
            profile_url = item.get("destination_url", "")

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

        # Message sending requires multi-step browser automation.
        logger.warning("LinkedIn message dispatch requires LLM agent orchestration via cc-browser")
        return SendResult(
            success=False,
            message="LinkedIn message dispatch not yet implemented via cc-browser. Use LLM agent with LinkedIn navigation skill."
        )

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
