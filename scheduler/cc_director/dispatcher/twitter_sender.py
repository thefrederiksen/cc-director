"""Twitter/X sender using cc-twitter CLI tool."""

import asyncio
import logging
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger("cc_director.dispatcher.twitter")


def _bin_dir() -> Path:
    """Resolve the cc-director bin directory."""
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "cc-director" / "bin"
    return Path.home() / ".cc-director" / "bin"


CC_TWITTER_PATH = _bin_dir() / "cc-twitter.exe"


@dataclass
class SendResult:
    """Result of a send operation."""

    success: bool
    message: str
    stdout: Optional[str] = None
    stderr: Optional[str] = None
    posted_url: Optional[str] = None


class TwitterSender:
    """Send tweets, replies, and threads via cc-twitter CLI."""

    def __init__(self, db_path: Optional[Path] = None):
        """Initialize the Twitter sender.

        Args:
            db_path: Path to SQLite database (for future media extraction)
        """
        self.db_path = db_path

    async def send(self, item: Dict[str, Any]) -> SendResult:
        """Send Twitter content using cc-twitter CLI.

        Args:
            item: Content item dictionary with platform=twitter

        Returns:
            SendResult indicating success/failure
        """
        item_type = item.get("type", "post").lower()
        twitter_specific = item.get("twitter_specific", {}) or {}

        # Check if this is a thread
        is_thread = twitter_specific.get("is_thread", False)
        if is_thread:
            return await self._send_thread(item)
        elif item_type == "reply":
            return await self._send_reply(item)
        else:  # post
            return await self._send_post(item)

    async def _send_post(self, item: Dict[str, Any]) -> SendResult:
        """Create a tweet."""
        content = item.get("content", "")
        if not content:
            return SendResult(
                success=False,
                message="Missing tweet content"
            )

        cmd = [str(CC_TWITTER_PATH), "post", content]
        return await self._execute_command(cmd, "post")

    async def _send_reply(self, item: Dict[str, Any]) -> SendResult:
        """Reply to a tweet."""
        content = item.get("content", "")
        twitter_specific = item.get("twitter_specific", {}) or {}
        reply_to = twitter_specific.get("reply_to", "") or item.get("context_url", "")

        if not reply_to:
            return SendResult(
                success=False,
                message="Missing reply_to URL in twitter_specific or context_url"
            )

        if not content:
            return SendResult(
                success=False,
                message="Missing reply content"
            )

        cmd = [str(CC_TWITTER_PATH), "reply", content, "--to", reply_to]
        return await self._execute_command(cmd, "reply")

    async def _send_thread(self, item: Dict[str, Any]) -> SendResult:
        """Post a tweet thread."""
        thread_content = item.get("thread_content", [])
        if not thread_content:
            # Fall back to content split by double newlines
            content = item.get("content", "")
            if not content:
                return SendResult(
                    success=False,
                    message="Missing thread content"
                )
            thread_content = [t.strip() for t in content.split("\n\n") if t.strip()]

        if not thread_content:
            return SendResult(
                success=False,
                message="Empty thread content"
            )

        cmd = [str(CC_TWITTER_PATH), "thread"] + thread_content
        return await self._execute_command(cmd, "thread")

    async def _execute_command(
        self,
        cmd: List[str],
        action_type: str
    ) -> SendResult:
        """Execute cc-twitter command.

        Args:
            cmd: Command and arguments
            action_type: Type of action (post, reply, thread)

        Returns:
            SendResult
        """
        try:
            logger.info(f"Sending Twitter {action_type}")
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
                logger.info(f"Twitter {action_type} sent successfully")
                return SendResult(
                    success=True,
                    message=f"Twitter {action_type} sent",
                    stdout=stdout_str,
                    stderr=stderr_str
                )
            else:
                logger.error(f"Twitter {action_type} failed: {stderr_str}")
                logger.error(f"stdout: {stdout_str}")
                return SendResult(
                    success=False,
                    message=f"Failed with exit code {result.returncode}",
                    stdout=stdout_str,
                    stderr=stderr_str
                )

        except FileNotFoundError:
            error_msg = f"cc-twitter not found at {CC_TWITTER_PATH}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
        except Exception as e:
            error_msg = f"Error sending Twitter {action_type}: {e}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
