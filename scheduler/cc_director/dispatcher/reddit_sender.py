"""Reddit sender using cc-reddit CLI tool."""

import asyncio
import logging
import os
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Dict, List, Optional

logger = logging.getLogger("cc_director.dispatcher.reddit")


def _bin_dir() -> Path:
    """Resolve the cc-director bin directory."""
    local = os.environ.get("LOCALAPPDATA")
    if local:
        return Path(local) / "cc-director" / "bin"
    return Path.home() / ".cc-director" / "bin"


CC_REDDIT_PATH = _bin_dir() / "cc-reddit.exe"


@dataclass
class SendResult:
    """Result of a send operation."""

    success: bool
    message: str
    stdout: Optional[str] = None
    stderr: Optional[str] = None
    posted_url: Optional[str] = None


class RedditSender:
    """Send Reddit posts, comments, and replies via cc-reddit CLI."""

    def __init__(self, db_path: Optional[Path] = None):
        """Initialize the Reddit sender.

        Args:
            db_path: Path to SQLite database (for future media extraction)
        """
        self.db_path = db_path

    async def send(self, item: Dict[str, Any]) -> SendResult:
        """Send Reddit content using cc-reddit CLI.

        Args:
            item: Content item dictionary with platform=reddit

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
        """Create a Reddit post."""
        content = item.get("content", "")
        reddit_specific = item.get("reddit_specific", {}) or {}

        subreddit = reddit_specific.get("subreddit", "")
        title = reddit_specific.get("title", "")

        if not subreddit:
            return SendResult(
                success=False,
                message="Missing subreddit in reddit_specific"
            )

        if not title:
            return SendResult(
                success=False,
                message="Missing title in reddit_specific"
            )

        # Build command: cc-reddit create <subreddit> --title "..." --body "..."
        cmd = [str(CC_REDDIT_PATH), "create", subreddit, "--title", title]

        if content:
            cmd.extend(["--body", content])

        # Add flair if specified
        flair = reddit_specific.get("flair", "")
        if flair:
            cmd.extend(["--flair", flair])

        return await self._execute_command(cmd, "post")

    async def _send_comment(self, item: Dict[str, Any]) -> SendResult:
        """Post a comment on Reddit."""
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

        cmd = [str(CC_REDDIT_PATH), "comment", context_url, "--text", content]
        return await self._execute_command(cmd, "comment")

    async def _send_reply(self, item: Dict[str, Any]) -> SendResult:
        """Reply to a Reddit comment."""
        content = item.get("content", "")
        context_url = item.get("context_url", "")

        if not context_url:
            return SendResult(
                success=False,
                message="Missing context_url for reply"
            )

        if not content:
            return SendResult(
                success=False,
                message="Missing reply content"
            )

        cmd = [str(CC_REDDIT_PATH), "reply", context_url, "--text", content]
        return await self._execute_command(cmd, "reply")

    async def _execute_command(
        self,
        cmd: List[str],
        action_type: str
    ) -> SendResult:
        """Execute cc-reddit command.

        Args:
            cmd: Command and arguments
            action_type: Type of action (post, comment, reply)

        Returns:
            SendResult
        """
        try:
            logger.info(f"Sending Reddit {action_type}")
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
                logger.info(f"Reddit {action_type} sent successfully")
                return SendResult(
                    success=True,
                    message=f"Reddit {action_type} sent",
                    stdout=stdout_str,
                    stderr=stderr_str
                )
            else:
                logger.error(f"Reddit {action_type} failed: {stderr_str}")
                logger.error(f"stdout: {stdout_str}")
                return SendResult(
                    success=False,
                    message=f"Failed with exit code {result.returncode}",
                    stdout=stdout_str,
                    stderr=stderr_str
                )

        except FileNotFoundError:
            error_msg = f"cc-reddit not found at {CC_REDDIT_PATH}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
        except Exception as e:
            error_msg = f"Error sending Reddit {action_type}: {e}"
            logger.error(error_msg)
            return SendResult(
                success=False,
                message=error_msg
            )
