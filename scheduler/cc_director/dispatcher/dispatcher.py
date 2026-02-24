"""Communication dispatcher - coordinates sending approved communications."""

import asyncio
import json
import logging
import shutil
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional

from .config import DispatcherConfig
from .email_sender import EmailSender

logger = logging.getLogger("cc_director.dispatcher")


class CommunicationDispatcher:
    """Coordinates sending approved communications."""

    def __init__(self, config: Optional[DispatcherConfig] = None):
        """
        Initialize the dispatcher.

        Args:
            config: Dispatcher configuration. Uses defaults if not provided.
        """
        self.config = config or DispatcherConfig()
        self.email_sender = EmailSender()
        self.config.ensure_folders_exist()

    async def dispatch_item(self, item_path: Path) -> bool:
        """
        Dispatch a single content item.

        Args:
            item_path: Path to the JSON file in approved/ folder

        Returns:
            True if successfully dispatched
        """
        try:
            # Load the item
            with open(item_path, "r", encoding="utf-8") as f:
                item = json.load(f)

            item_id = item.get("id", "unknown")[:8]
            platform = item.get("platform", "unknown")
            logger.info(f"Processing item {item_id} for {platform}")

            # Check timing
            send_timing = item.get("send_timing", "asap").lower()

            if send_timing == "hold":
                logger.info(f"Item {item_id} is on hold, skipping")
                return False

            if send_timing == "scheduled":
                scheduled_for = item.get("scheduled_for")
                if scheduled_for:
                    try:
                        scheduled_dt = datetime.fromisoformat(scheduled_for.replace("Z", "+00:00"))
                        if scheduled_dt > datetime.now(scheduled_dt.tzinfo):
                            logger.info(f"Item {item_id} scheduled for {scheduled_for}, not time yet")
                            return False
                    except (ValueError, TypeError) as e:
                        logger.warning(f"Invalid scheduled_for format: {scheduled_for}, sending now")

            # Dispatch by platform
            if platform == "email":
                result = await self.email_sender.send(item)
                if result.success:
                    await self._mark_posted(item_path, item, result.message)
                    return True
                else:
                    logger.error(f"Failed to send email {item_id}: {result.message}")
                    return False
            else:
                logger.warning(f"Platform {platform} not yet supported for dispatch")
                return False

        except json.JSONDecodeError as e:
            logger.error(f"Invalid JSON in {item_path}: {e}")
            return False
        except Exception as e:
            logger.error(f"Error dispatching {item_path}: {e}")
            return False

    async def _mark_posted(
        self,
        item_path: Path,
        item: Dict[str, Any],
        message: str
    ) -> None:
        """
        Update item with posted metadata and move to posted/ folder.

        Args:
            item_path: Original path in approved/
            item: Content item dictionary
            message: Success message to record
        """
        # Update item metadata
        item["status"] = "posted"
        item["posted_at"] = datetime.utcnow().isoformat() + "Z"
        item["posted_by"] = "cc_director_service"
        item["posted_message"] = message

        # Generate new filename in posted/
        posted_path = self.config.posted_path / item_path.name

        # Write to posted/ folder
        with open(posted_path, "w", encoding="utf-8") as f:
            json.dump(item, f, indent=2)

        # Delete from approved/
        item_path.unlink()

        item_id = item.get("id", "unknown")[:8]
        logger.info(f"Item {item_id} moved to posted/")

    async def process_approved_items(self) -> int:
        """
        Process all items in the approved/ folder.

        Returns:
            Number of items successfully dispatched
        """
        approved_path = self.config.approved_path
        if not approved_path.exists():
            return 0

        dispatched = 0
        for item_path in approved_path.glob("*.json"):
            if await self.dispatch_item(item_path):
                dispatched += 1

        return dispatched

    async def process_scheduled_items(self) -> int:
        """
        Check approved items for any that are now due for sending.

        This is called periodically to handle scheduled items.

        Returns:
            Number of items dispatched
        """
        return await self.process_approved_items()

    def get_pending_count(self) -> int:
        """Get count of items pending dispatch in approved/ folder."""
        if not self.config.approved_path.exists():
            return 0
        return len(list(self.config.approved_path.glob("*.json")))

    def get_stats(self) -> Dict[str, int]:
        """Get dispatcher statistics."""
        stats = {
            "pending_review": 0,
            "approved": 0,
            "rejected": 0,
            "posted": 0,
        }

        for folder, key in [
            (self.config.pending_path, "pending_review"),
            (self.config.approved_path, "approved"),
            (self.config.rejected_path, "rejected"),
            (self.config.posted_path, "posted"),
        ]:
            if folder.exists():
                stats[key] = len(list(folder.glob("*.json")))

        return stats
