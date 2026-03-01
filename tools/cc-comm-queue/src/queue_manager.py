"""Queue operations for Communication Manager."""

import logging
from pathlib import Path
from typing import Any, Dict, List, Optional

# Handle imports for both package and frozen executable
try:
    from .schema import ContentItem, QueueResult, QueueStats, Status
    from .database import Database
except ImportError:
    from schema import ContentItem, QueueResult, QueueStats, Status
    from database import Database

logger = logging.getLogger(__name__)


class QueueManager:
    """Manages the communication content queue using SQLite database."""

    def __init__(self, queue_path: Path):
        """Initialize the queue manager.

        Args:
            queue_path: Path to the content directory (contains communications.db)
        """
        self.queue_path = queue_path
        self.db = Database(queue_path)

        # Keep paths for backward compatibility (migration)
        self.pending_path = queue_path / "pending_review"
        self.approved_path = queue_path / "approved"
        self.rejected_path = queue_path / "rejected"
        self.posted_path = queue_path / "posted"

    def ensure_directories(self) -> None:
        """Ensure content directory exists (for backward compatibility)."""
        self.queue_path.mkdir(parents=True, exist_ok=True)

    def add_content(self, item: ContentItem, media_files: Optional[List[Path]] = None) -> QueueResult:
        """Add a content item to the pending_review queue.

        Args:
            item: The content item to add
            media_files: Optional list of media file paths to attach

        Returns:
            QueueResult with success status and ticket number
        """
        try:
            ticket_number = self.db.add_communication(item)

            # Add additional media files if provided
            if media_files:
                for media_path in media_files:
                    if media_path.exists():
                        media_type = self._guess_media_type(media_path)
                        self.db.add_media(ticket_number, media_path, media_type)

            logger.info("Added content to queue: ticket #%d", ticket_number)
            return QueueResult(
                success=True,
                id=item.id,
                file=f"ticket #{ticket_number}",
            )

        except Exception as e:
            logger.error("Failed to add content: %s", e)
            return QueueResult(
                success=False,
                error=f"Failed to add content: {e}",
            )

    def _guess_media_type(self, path: Path) -> str:
        """Guess media type from file extension."""
        ext = path.suffix.lower()
        if ext in [".jpg", ".jpeg", ".png", ".gif", ".svg", ".webp"]:
            return "image"
        elif ext in [".mp4", ".webm", ".mov", ".avi"]:
            return "video"
        else:
            return "document"

    def list_content(self, status: Optional[Status] = None, limit: int = 100) -> List[Dict[str, Any]]:
        """List content items, optionally filtered by status.

        Args:
            status: Filter by status, or None for all
            limit: Maximum results

        Returns:
            List of content item dictionaries
        """
        return self.db.list_by_status(status, limit)

    def get_stats(self) -> QueueStats:
        """Get queue statistics.

        Returns:
            QueueStats with counts for each status
        """
        stats = self.db.get_stats()
        return QueueStats(
            pending_review=stats.get("pending_review", 0),
            approved=stats.get("approved", 0),
            rejected=stats.get("rejected", 0),
            posted=stats.get("posted", 0),
        )

    def get_content_by_id(self, content_id: str) -> Optional[Dict[str, Any]]:
        """Get a content item by ID.

        Args:
            content_id: The content item ID (or partial ID)

        Returns:
            Content item dictionary or None if not found
        """
        return self.db.get_by_id(content_id)

    def get_content_by_ticket(self, ticket_number: int) -> Optional[Dict[str, Any]]:
        """Get a content item by ticket number.

        Args:
            ticket_number: The ticket number

        Returns:
            Content item dictionary or None if not found
        """
        return self.db.get_by_ticket(ticket_number)

    def approve_content(self, ticket_number: int) -> bool:
        """Approve a content item.

        Args:
            ticket_number: The ticket number

        Returns:
            True if successful
        """
        return self.db.update_status(ticket_number, Status.APPROVED)

    def reject_content(self, ticket_number: int, reason: Optional[str] = None) -> bool:
        """Reject a content item.

        Args:
            ticket_number: The ticket number
            reason: Optional rejection reason

        Returns:
            True if successful
        """
        from datetime import datetime
        return self.db.update_status(
            ticket_number,
            Status.REJECTED,
            rejection_reason=reason,
            rejected_at=datetime.now().isoformat(),
            rejected_by="user",
        )

    def mark_posted(self, ticket_number: int, posted_by: str = "cc_director", posted_url: Optional[str] = None) -> bool:
        """Mark a content item as posted.

        Args:
            ticket_number: The ticket number
            posted_by: Who/what posted it
            posted_url: Optional URL where it was posted

        Returns:
            True if successful
        """
        from datetime import datetime
        return self.db.update_status(
            ticket_number,
            Status.POSTED,
            posted_at=datetime.now().isoformat(),
            posted_by=posted_by,
            posted_url=posted_url,
        )

    def move_to_review(self, ticket_number: int) -> bool:
        """Move a content item back to pending review.

        Args:
            ticket_number: The ticket number

        Returns:
            True if successful
        """
        return self.db.update_status(
            ticket_number,
            Status.PENDING_REVIEW,
            rejection_reason=None,
            rejected_at=None,
            rejected_by=None,
        )

    def update_content(self, ticket_number: int, content: str) -> bool:
        """Update the content of an item.

        Args:
            ticket_number: The ticket number
            content: The new content

        Returns:
            True if successful
        """
        return self.db.update_content(ticket_number, content)

    def delete_content(self, ticket_number: int) -> bool:
        """Delete a content item.

        Args:
            ticket_number: The ticket number

        Returns:
            True if successful
        """
        return self.db.delete_communication(ticket_number)

    def add_media(self, ticket_number: int, file_path: Path, media_type: str = "image", alt_text: Optional[str] = None) -> int:
        """Add a media file to a communication as BLOB.

        Args:
            ticket_number: The ticket number
            file_path: Path to the media file
            media_type: Type of media
            alt_text: Optional alt text

        Returns:
            The media ID
        """
        return self.db.add_media(ticket_number, file_path, media_type, alt_text)

    def get_media_data(self, media_id: int) -> Optional[bytes]:
        """Retrieve media BLOB data by ID.

        Args:
            media_id: The media ID

        Returns:
            The file bytes or None if not found
        """
        return self.db.get_media_data(media_id)

    def get_media(self, ticket_number: int) -> List[Dict[str, Any]]:
        """Get media files for a communication.

        Args:
            ticket_number: The ticket number

        Returns:
            List of media dictionaries
        """
        return self.db.get_media(ticket_number)

    def search(self, query: str, limit: int = 50) -> List[Dict[str, Any]]:
        """Search communications by content.

        Args:
            query: Search query
            limit: Maximum results

        Returns:
            List of matching communications
        """
        return self.db.search(query, limit)

    def close(self) -> None:
        """Close database connection."""
        self.db.close()
