"""SQLite database watcher for approved communications."""

import asyncio
import json
import logging
import sqlite3
from datetime import datetime
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional, Set

logger = logging.getLogger("cc_director.dispatcher.sqlite_watcher")


class SQLiteWatcher:
    """Polls SQLite database for approved items ready to dispatch.

    Watches the communications table for items with:
    - status = 'approved'
    - send_timing = 'immediate' or 'asap' (dispatch immediately)
    - send_timing = 'scheduled' AND scheduled_for <= now (dispatch when ready)
    - send_timing = 'hold' (ignored, requires manual dispatch)
    """

    def __init__(
        self,
        db_path: Path,
        callback: Callable[[Dict[str, Any]], None],
        poll_interval: float = 5.0
    ):
        """Initialize the SQLite watcher.

        Args:
            db_path: Path to SQLite database
            callback: Async callback function to call for each item
            poll_interval: Seconds between polls
        """
        self.db_path = db_path
        self.callback = callback
        self.poll_interval = poll_interval
        self._running = False
        self._processing: Set[int] = set()  # Track items being processed

    async def start(self) -> None:
        """Start the watcher loop."""
        logger.info(f"Starting SQLite watcher on {self.db_path}")
        logger.info(f"Poll interval: {self.poll_interval}s")
        self._running = True

        while self._running:
            try:
                await self._check_for_items()
            except Exception as e:
                logger.error(f"Error in watcher loop: {e}")

            await asyncio.sleep(self.poll_interval)

    def stop(self) -> None:
        """Stop the watcher loop."""
        logger.info("Stopping SQLite watcher")
        self._running = False

    async def _check_for_items(self) -> None:
        """Check for approved items ready to dispatch."""
        if not self.db_path.exists():
            logger.warning(f"Database not found: {self.db_path}")
            return

        now = datetime.utcnow().isoformat()

        try:
            conn = sqlite3.connect(self.db_path)
            conn.row_factory = sqlite3.Row

            # Find items ready to dispatch:
            # 1. status = 'approved'
            # 2. Either immediate/asap OR scheduled time has passed
            cursor = conn.execute("""
                SELECT * FROM communications
                WHERE status = 'approved'
                AND (
                    send_timing IN ('immediate', 'asap')
                    OR (send_timing = 'scheduled' AND scheduled_for <= ?)
                )
            """, (now,))

            rows = cursor.fetchall()

            # Load media for each item
            items = []
            for row in rows:
                item = dict(row)
                ticket_number = item.get("ticket_number")

                # Skip if already being processed
                if ticket_number in self._processing:
                    continue

                # Parse JSON fields
                item = self._parse_json_fields(item)

                # Load media
                media_cursor = conn.execute("""
                    SELECT id, type, filename, alt_text, file_size, mime_type
                    FROM media
                    WHERE communication_id = ?
                """, (item.get("id"),))

                item["media"] = [dict(m) for m in media_cursor.fetchall()]

                items.append(item)

            conn.close()

            # Process each item
            for item in items:
                ticket_number = item.get("ticket_number")
                self._processing.add(ticket_number)

                try:
                    logger.info(
                        f"Dispatching #{ticket_number}: "
                        f"{item.get('platform')} {item.get('type')}"
                    )
                    await self.callback(item)
                except Exception as e:
                    logger.error(f"Error dispatching #{ticket_number}: {e}")
                finally:
                    self._processing.discard(ticket_number)

        except sqlite3.Error as e:
            logger.error(f"SQLite error: {e}")

    def _parse_json_fields(self, item: Dict[str, Any]) -> Dict[str, Any]:
        """Parse JSON string fields back to dicts/lists.

        Args:
            item: Raw database row as dict

        Returns:
            Item with parsed JSON fields
        """
        json_fields = [
            "tags",
            "recipient",
            "linkedin_specific",
            "twitter_specific",
            "reddit_specific",
            "email_specific",
            "article_specific",
            "thread_content"
        ]

        for field in json_fields:
            value = item.get(field)
            if value and isinstance(value, str):
                try:
                    item[field] = json.loads(value)
                except json.JSONDecodeError:
                    pass  # Keep as string if not valid JSON

        return item


class SQLiteDispatcher:
    """Coordinates dispatching approved items from SQLite."""

    def __init__(
        self,
        db_path: Path,
        poll_interval: float = 5.0
    ):
        """Initialize the dispatcher.

        Args:
            db_path: Path to SQLite database
            poll_interval: Seconds between polls
        """
        self.db_path = db_path
        self.poll_interval = poll_interval
        self.watcher: Optional[SQLiteWatcher] = None

        # Import senders
        from .email_sender import EmailSender
        from .linkedin_sender import LinkedInSender

        self.email_sender = EmailSender()
        self.linkedin_sender = LinkedInSender(db_path=db_path)

    async def start(self) -> None:
        """Start the dispatcher."""
        self.watcher = SQLiteWatcher(
            db_path=self.db_path,
            callback=self._dispatch_item,
            poll_interval=self.poll_interval
        )
        await self.watcher.start()

    def stop(self) -> None:
        """Stop the dispatcher."""
        if self.watcher:
            self.watcher.stop()

    async def _dispatch_item(self, item: Dict[str, Any]) -> None:
        """Dispatch a single item to the appropriate sender.

        Args:
            item: Content item to dispatch
        """
        platform = item.get("platform", "").lower()
        ticket_number = item.get("ticket_number")

        logger.info(f"Dispatching #{ticket_number} to {platform}")

        try:
            if platform == "email":
                result = await self.email_sender.send(item)
            elif platform == "linkedin":
                result = await self.linkedin_sender.send(item)
            else:
                logger.warning(f"Unknown platform: {platform}")
                result = type("Result", (), {
                    "success": False,
                    "message": f"Unknown platform: {platform}"
                })()

            if result.success:
                await self._mark_as_posted(ticket_number)
                logger.info(f"#{ticket_number} dispatched successfully")
            else:
                logger.error(f"#{ticket_number} dispatch failed: {result.message}")
                # Optionally: mark as failed or retry

        except Exception as e:
            logger.error(f"Error dispatching #{ticket_number}: {e}")

    async def _mark_as_posted(self, ticket_number: int) -> None:
        """Update item status to 'posted' in database.

        Args:
            ticket_number: Ticket number to update
        """
        try:
            conn = sqlite3.connect(self.db_path)
            now = datetime.utcnow().isoformat()

            conn.execute("""
                UPDATE communications
                SET status = 'posted',
                    posted_at = ?,
                    posted_by = 'cc_director'
                WHERE ticket_number = ?
            """, (now, ticket_number))

            conn.commit()
            conn.close()

            logger.info(f"#{ticket_number} marked as posted")

        except sqlite3.Error as e:
            logger.error(f"Error marking #{ticket_number} as posted: {e}")
