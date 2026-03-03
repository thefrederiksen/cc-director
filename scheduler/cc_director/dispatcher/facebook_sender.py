"""Facebook sender using cc-facebook CLI tool."""

import asyncio
import logging
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger("cc_director.dispatcher.facebook")


def _bin_dir() -> Path:
    """Resolve the cc-director bin directory."""
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "cc-director" / "bin"
    return Path.home() / ".cc-director" / "bin"


CC_FACEBOOK_PATH = _bin_dir() / "cc-facebook.exe"


@dataclass
class SendResult:
    """Result of a send operation."""

    success: bool
    message: str
    stdout: Optional[str] = None
    stderr: Optional[str] = None
    posted_url: Optional[str] = None


class FacebookSender:
    """Send Facebook page posts and comments via cc-facebook CLI."""

    def __init__(self, db_path: Optional[Path] = None):
        """Initialize the Facebook sender.

        Args:
            db_path: Path to SQLite database (for future media extraction)
        """
        self.db_path = db_path

    async def send(self, item: Dict[str, Any]) -> SendResult:
        """Send Facebook content using cc-facebook CLI.

        Args:
            item: Content item dictionary with platform=facebook

        Returns:
            SendResult indicating success/failure
        """
        item_type = item.get("type", "post").lower()

        if item_type == "comment":
            return await self._send_comment(item)
        elif item_type == "reply":
            return await self._send_reply(item)
        else:  # post
            return await self._send_post(item)

    async def _send_post(self, item: Dict[str, Any]) -> SendResult:
        """Create a Facebook page post."""
        content = item.get("content", "")
        if not content:
            return SendResult(
                success=False,
                message="Missing post content"
            )

        cmd = [str(CC_FACEBOOK_PATH), "post", content]

        # Add link if present in destination_url
        link = item.get("destination_url", "")
        if link:
            cmd.extend(["--link", link])

        return await self._execute_command(cmd, "post")

    async def _send_comment(self, item: Dict[str, Any]) -> SendResult:
        """Comment on a Facebook post."""
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

        cmd = [str(CC_FACEBOOK_PATH), "comment", context_url, content]
        return await self._execute_command(cmd, "comment")

    async def _send_reply(self, item: Dict[str, Any]) -> SendResult:
        """Reply to a Facebook comment."""
        content = item.get("content", "")
        facebook_specific = item.get("facebook_specific", {}) or {}
        comment_id = facebook_specific.get("parent_comment_id", "") or item.get("context_url", "")

        if not comment_id:
            return SendResult(
                success=False,
                message="Missing parent_comment_id in facebook_specific or context_url"
            )

        if not content:
            return SendResult(
                success=False,
                message="Missing reply content"
            )

        cmd = [str(CC_FACEBOOK_PATH), "reply", comment_id, content]
        return await self._execute_command(cmd, "reply")

    async def _execute_command(
        self,
        cmd: List[str],
        action_type: str
    ) -> SendResult:
        """Execute cc-facebook command.

        Args:
            cmd: Command and arguments
            action_type: Type of action (post, comment, reply)

        Returns:
            SendResult
        """
        try:
            logger.info(f"Sending Facebook {action_type}")
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
                logger.info(f"Facebook {action_type} sent successfully")
                return SendResult(
                    success=True,
                    message=f"Facebook {action_type} sent",
                    stdout=stdout_str,
                    stderr=stderr_str
                )
            else:
                logger.error(f"Facebook {action_type} failed: {stderr_str}")
                logger.error(f"stdout: {stdout_str}")
                return SendResult(
                    success=False,
                    message=f"Failed with exit code {result.returncode}",
                    stdout=stdout_str,
                    stderr=stderr_str
                )

        except FileNotFoundError:
            error_msg = f"cc-facebook not found at {CC_FACEBOOK_PATH}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
        except Exception as e:
            error_msg = f"Error sending Facebook {action_type}: {e}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
