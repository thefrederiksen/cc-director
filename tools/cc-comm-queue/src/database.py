"""SQLite database layer for Communication Manager."""

import json
import logging
import sqlite3
from pathlib import Path
from typing import Any, Dict, List, Optional

# Handle imports for both package and frozen executable
try:
    from .schema import ContentItem, MediaItem, Status
except ImportError:
    from schema import ContentItem, MediaItem, Status

logger = logging.getLogger(__name__)

# Database schema
SCHEMA_SQL = """
-- Main communications table
CREATE TABLE IF NOT EXISTS communications (
    id TEXT PRIMARY KEY,
    ticket_number INTEGER UNIQUE NOT NULL,
    platform TEXT NOT NULL,
    type TEXT NOT NULL,
    persona TEXT NOT NULL,
    persona_display TEXT,
    content TEXT NOT NULL,

    -- Timestamps
    created_at TEXT NOT NULL,
    created_by TEXT DEFAULT 'claude_code',
    posted_at TEXT,
    posted_by TEXT,
    posted_url TEXT,
    post_id TEXT,
    rejected_at TEXT,
    rejected_by TEXT,
    rejection_reason TEXT,
    scheduled_for TEXT,

    -- Status workflow
    status TEXT NOT NULL DEFAULT 'pending_review',

    -- Send options
    send_timing TEXT DEFAULT 'asap',
    send_from TEXT,

    -- Context
    context_url TEXT,
    context_title TEXT,
    context_author TEXT,
    destination_url TEXT,

    -- Metadata
    campaign_id TEXT,
    notes TEXT,
    tags TEXT,  -- JSON array

    -- Platform-specific (stored as JSON)
    linkedin_specific TEXT,
    twitter_specific TEXT,
    reddit_specific TEXT,
    email_specific TEXT,
    article_specific TEXT,

    -- Other
    recipient TEXT,  -- JSON object
    thread_content TEXT  -- JSON array
);

-- Media/attachments (stored as BLOB for full portability)
CREATE TABLE IF NOT EXISTS media (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    communication_id TEXT NOT NULL REFERENCES communications(id) ON DELETE CASCADE,
    type TEXT NOT NULL,
    filename TEXT NOT NULL,
    data BLOB NOT NULL,
    alt_text TEXT,
    file_size INTEGER,
    mime_type TEXT
);

-- Indexes for common queries
CREATE INDEX IF NOT EXISTS idx_status ON communications(status);
CREATE INDEX IF NOT EXISTS idx_platform ON communications(platform);
CREATE INDEX IF NOT EXISTS idx_created_at ON communications(created_at);
CREATE INDEX IF NOT EXISTS idx_posted_at ON communications(posted_at);
CREATE INDEX IF NOT EXISTS idx_ticket_number ON communications(ticket_number);
CREATE INDEX IF NOT EXISTS idx_media_comm_id ON media(communication_id);
"""


class Database:
    """SQLite database wrapper for Communication Manager."""

    def __init__(self, content_path: Path):
        """Initialize database connection.

        Args:
            content_path: Path to the content directory (contains communications.db)
        """
        self.content_path = content_path
        self.db_path = content_path / "communications.db"
        self.conn: Optional[sqlite3.Connection] = None

        # Ensure content directory exists
        self.content_path.mkdir(parents=True, exist_ok=True)

        self._connect()
        self._init_schema()

    def _connect(self) -> None:
        """Establish database connection."""
        self.conn = sqlite3.connect(str(self.db_path), check_same_thread=False)
        self.conn.row_factory = sqlite3.Row
        # Enable foreign keys
        self.conn.execute("PRAGMA foreign_keys = ON")

    def _init_schema(self) -> None:
        """Initialize database schema."""
        if self.conn is None:
            raise RuntimeError("Database not connected")

        self.conn.executescript(SCHEMA_SQL)
        self.conn.commit()

    def close(self) -> None:
        """Close database connection."""
        if self.conn:
            self.conn.close()
            self.conn = None

    def _get_next_ticket_number(self) -> int:
        """Get the next ticket number.

        Returns:
            The next unique ticket number
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            "SELECT COALESCE(MAX(ticket_number), 0) + 1 FROM communications"
        )
        result = cursor.fetchone()
        return result[0] if result else 1

    def add_communication(self, item: ContentItem) -> int:
        """Add a new communication to the database.

        Args:
            item: The ContentItem to add

        Returns:
            The assigned ticket number
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        # Assign ticket number if not set
        if item.ticket_number is None:
            item.ticket_number = self._get_next_ticket_number()

        # Convert complex fields to JSON
        tags_json = json.dumps(item.tags) if item.tags else None
        linkedin_json = item.linkedin_specific.model_dump_json() if item.linkedin_specific else None
        twitter_json = item.twitter_specific.model_dump_json() if item.twitter_specific else None
        reddit_json = item.reddit_specific.model_dump_json() if item.reddit_specific else None
        email_json = item.email_specific.model_dump_json() if item.email_specific else None
        article_json = item.article_specific.model_dump_json() if item.article_specific else None
        recipient_json = item.recipient.model_dump_json() if item.recipient else None
        thread_json = json.dumps(item.thread_content) if item.thread_content else None

        self.conn.execute(
            """
            INSERT INTO communications (
                id, ticket_number, platform, type, persona, persona_display, content,
                created_at, created_by, posted_at, posted_by, posted_url, post_id,
                rejected_at, rejected_by, rejection_reason, scheduled_for,
                status, send_timing, send_from,
                context_url, context_title, context_author, destination_url,
                campaign_id, notes, tags,
                linkedin_specific, twitter_specific, reddit_specific,
                email_specific, article_specific, recipient, thread_content
            ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
            """,
            (
                item.id,
                item.ticket_number,
                item.platform.value,
                item.type.value,
                item.persona.value,
                item.persona_display,
                item.content,
                item.created_at,
                item.created_by,
                item.posted_at,
                item.posted_by,
                item.posted_url,
                item.post_id,
                item.rejected_at,
                item.rejected_by,
                item.rejection_reason,
                item.scheduled_for,
                item.status.value,
                item.send_timing.value,
                item.send_from,
                item.context_url,
                item.context_title,
                item.context_author,
                item.destination_url,
                item.campaign_id,
                item.notes,
                tags_json,
                linkedin_json,
                twitter_json,
                reddit_json,
                email_json,
                article_json,
                recipient_json,
                thread_json,
            ),
        )
        self.conn.commit()

        # Add media if present
        if item.media:
            for media_item in item.media:
                self._add_media_record(item.id, item.ticket_number, media_item)

        return item.ticket_number

    def _add_media_record(
        self, communication_id: str, ticket_number: int, media_item: MediaItem
    ) -> int:
        """Add a media record to the database as BLOB.

        Args:
            communication_id: The parent communication ID
            ticket_number: The ticket number (unused, kept for API compatibility)
            media_item: The media item to add

        Returns:
            The media ID
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        source_path = Path(media_item.path)
        if not source_path.exists():
            raise FileNotFoundError(f"Media file not found: {source_path}")

        # Read file content as bytes
        with open(source_path, "rb") as f:
            file_data = f.read()

        file_size = len(file_data)
        mime_type = self._guess_mime_type(source_path)
        filename = source_path.name

        cursor = self.conn.execute(
            """
            INSERT INTO media (
                communication_id, type, filename, data, alt_text, file_size, mime_type
            ) VALUES (?, ?, ?, ?, ?, ?, ?)
            """,
            (
                communication_id,
                media_item.type,
                filename,
                file_data,
                media_item.alt_text,
                file_size,
                mime_type,
            ),
        )
        self.conn.commit()

        return cursor.lastrowid

    def add_media(self, ticket_number: int, file_path: Path, media_type: str = "image", alt_text: Optional[str] = None) -> int:
        """Add media file to a communication as BLOB.

        Args:
            ticket_number: The ticket number
            file_path: Path to the media file
            media_type: Type of media (image, video, document)
            alt_text: Optional alt text

        Returns:
            The media ID
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        # Get communication ID
        cursor = self.conn.execute(
            "SELECT id FROM communications WHERE ticket_number = ?",
            (ticket_number,)
        )
        row = cursor.fetchone()
        if not row:
            raise ValueError(f"Communication not found: ticket #{ticket_number}")

        communication_id = row["id"]
        media_item = MediaItem(type=media_type, path=str(file_path), alt_text=alt_text)
        return self._add_media_record(communication_id, ticket_number, media_item)

    def get_media_data(self, media_id: int) -> Optional[bytes]:
        """Retrieve media BLOB data by ID.

        Args:
            media_id: The media ID

        Returns:
            The file bytes or None if not found
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            "SELECT data FROM media WHERE id = ?", (media_id,)
        )
        row = cursor.fetchone()
        return row["data"] if row else None

    def get_media_info(self, media_id: int) -> Optional[Dict[str, Any]]:
        """Retrieve media info (without BLOB data) by ID.

        Args:
            media_id: The media ID

        Returns:
            Dictionary with media info or None if not found
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            "SELECT id, communication_id, type, filename, alt_text, file_size, mime_type FROM media WHERE id = ?",
            (media_id,)
        )
        row = cursor.fetchone()
        return dict(row) if row else None

    def _guess_mime_type(self, path: Path) -> str:
        """Guess MIME type from file extension."""
        ext = path.suffix.lower()
        mime_types = {
            ".jpg": "image/jpeg",
            ".jpeg": "image/jpeg",
            ".png": "image/png",
            ".gif": "image/gif",
            ".svg": "image/svg+xml",
            ".webp": "image/webp",
            ".mp4": "video/mp4",
            ".webm": "video/webm",
            ".pdf": "application/pdf",
            ".doc": "application/msword",
            ".docx": "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls": "application/vnd.ms-excel",
            ".xlsx": "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        }
        return mime_types.get(ext, "application/octet-stream")

    def get_by_id(self, communication_id: str) -> Optional[Dict[str, Any]]:
        """Get a communication by ID.

        Args:
            communication_id: The communication ID (can be partial)

        Returns:
            Communication data dictionary or None
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            "SELECT * FROM communications WHERE id LIKE ?",
            (f"{communication_id}%",)
        )
        row = cursor.fetchone()
        if not row:
            return None

        return self._row_to_dict(row)

    def get_by_ticket(self, ticket_number: int) -> Optional[Dict[str, Any]]:
        """Get a communication by ticket number.

        Args:
            ticket_number: The ticket number

        Returns:
            Communication data dictionary or None
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            "SELECT * FROM communications WHERE ticket_number = ?",
            (ticket_number,)
        )
        row = cursor.fetchone()
        if not row:
            return None

        return self._row_to_dict(row)

    def list_by_status(self, status: Optional[Status] = None, limit: int = 100) -> List[Dict[str, Any]]:
        """List communications by status.

        Args:
            status: Filter by status, or None for all
            limit: Maximum number of results

        Returns:
            List of communication dictionaries
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        if status:
            cursor = self.conn.execute(
                "SELECT * FROM communications WHERE status = ? ORDER BY created_at DESC LIMIT ?",
                (status.value, limit)
            )
        else:
            cursor = self.conn.execute(
                "SELECT * FROM communications ORDER BY created_at DESC LIMIT ?",
                (limit,)
            )

        return [self._row_to_dict(row) for row in cursor.fetchall()]

    def update_status(self, ticket_number: int, new_status: Status, **kwargs: Any) -> bool:
        """Update communication status.

        Args:
            ticket_number: The ticket number
            new_status: The new status
            **kwargs: Additional fields to update (e.g., rejection_reason, posted_at)

        Returns:
            True if successful
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        # Build update query
        fields = ["status = ?"]
        values: List[Any] = [new_status.value]

        for key, value in kwargs.items():
            fields.append(f"{key} = ?")
            values.append(value)

        values.append(ticket_number)

        self.conn.execute(
            f"UPDATE communications SET {', '.join(fields)} WHERE ticket_number = ?",
            values
        )
        self.conn.commit()
        return self.conn.total_changes > 0

    def update_content(self, ticket_number: int, content: str) -> bool:
        """Update communication content.

        Args:
            ticket_number: The ticket number
            content: The new content

        Returns:
            True if successful
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        self.conn.execute(
            "UPDATE communications SET content = ? WHERE ticket_number = ?",
            (content, ticket_number)
        )
        self.conn.commit()
        return self.conn.total_changes > 0

    def delete_communication(self, ticket_number: int) -> bool:
        """Delete a communication and its media.

        Args:
            ticket_number: The ticket number

        Returns:
            True if successful
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        # Delete from database (media records cascade deleted via foreign key)
        cursor = self.conn.execute(
            "DELETE FROM communications WHERE ticket_number = ?",
            (ticket_number,)
        )
        self.conn.commit()
        return cursor.rowcount > 0

    def get_media(self, ticket_number: int) -> List[Dict[str, Any]]:
        """Get media metadata for a communication (excludes BLOB data).

        Args:
            ticket_number: The ticket number

        Returns:
            List of media dictionaries (without data field)
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            """
            SELECT m.id, m.communication_id, m.type, m.filename, m.alt_text, m.file_size, m.mime_type
            FROM media m
            JOIN communications c ON m.communication_id = c.id
            WHERE c.ticket_number = ?
            """,
            (ticket_number,)
        )
        return [dict(row) for row in cursor.fetchall()]

    def get_stats(self) -> Dict[str, int]:
        """Get queue statistics.

        Returns:
            Dictionary with counts for each status
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            """
            SELECT status, COUNT(*) as count
            FROM communications
            GROUP BY status
            """
        )

        stats = {
            "pending_review": 0,
            "approved": 0,
            "rejected": 0,
            "posted": 0,
        }

        for row in cursor.fetchall():
            stats[row["status"]] = row["count"]

        return stats

    def _row_to_dict(self, row: sqlite3.Row) -> Dict[str, Any]:
        """Convert a database row to a dictionary with parsed JSON fields."""
        data = dict(row)

        # Parse JSON fields
        json_fields = [
            "tags", "linkedin_specific", "twitter_specific", "reddit_specific",
            "email_specific", "article_specific", "recipient", "thread_content"
        ]

        for field in json_fields:
            if data.get(field):
                try:
                    data[field] = json.loads(data[field])
                except json.JSONDecodeError:
                    pass

        # Add media (metadata only, no BLOB data)
        if self.conn:
            cursor = self.conn.execute(
                "SELECT id, type, filename, alt_text, file_size, mime_type FROM media WHERE communication_id = ?",
                (data["id"],)
            )
            media_rows = cursor.fetchall()
            if media_rows:
                data["media"] = [
                    {
                        "id": m["id"],
                        "type": m["type"],
                        "filename": m["filename"],
                        "alt_text": m["alt_text"],
                        "file_size": m["file_size"],
                        "mime_type": m["mime_type"],
                    }
                    for m in media_rows
                ]

        return data

    def search(self, query: str, limit: int = 50) -> List[Dict[str, Any]]:
        """Search communications by content.

        Args:
            query: Search query
            limit: Maximum results

        Returns:
            List of matching communications
        """
        if self.conn is None:
            raise RuntimeError("Database not connected")

        cursor = self.conn.execute(
            """
            SELECT * FROM communications
            WHERE content LIKE ? OR notes LIKE ? OR context_title LIKE ?
            ORDER BY created_at DESC
            LIMIT ?
            """,
            (f"%{query}%", f"%{query}%", f"%{query}%", limit)
        )
        return [self._row_to_dict(row) for row in cursor.fetchall()]
