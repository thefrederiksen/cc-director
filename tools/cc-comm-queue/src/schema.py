"""Pydantic models for Communication Manager content schema."""

from datetime import datetime
from enum import Enum
from typing import Any, Dict, List, Optional
from uuid import uuid4

from pydantic import BaseModel, Field


class Platform(str, Enum):
    """Supported platforms."""
    LINKEDIN = "linkedin"
    TWITTER = "twitter"
    REDDIT = "reddit"
    YOUTUBE = "youtube"
    EMAIL = "email"
    BLOG = "blog"


class ContentType(str, Enum):
    """Content types."""
    POST = "post"
    COMMENT = "comment"
    REPLY = "reply"
    MESSAGE = "message"
    ARTICLE = "article"
    EMAIL = "email"


class Persona(str, Enum):
    """Persona options."""
    MINDZIE = "mindzie"
    CENTER_CONSULTING = "center_consulting"
    PERSONAL = "personal"


class Status(str, Enum):
    """Content status."""
    PENDING_REVIEW = "pending_review"
    APPROVED = "approved"
    REJECTED = "rejected"
    POSTED = "posted"


class SendTiming(str, Enum):
    """When to send the content after approval."""
    IMMEDIATE = "immediate"    # Send right away when approved
    SCHEDULED = "scheduled"    # Send at scheduled_for datetime
    ASAP = "asap"              # Send as soon as possible (same as immediate)
    HOLD = "hold"              # Don't auto-send, requires manual dispatch


class Visibility(str, Enum):
    """LinkedIn visibility options."""
    PUBLIC = "public"
    CONNECTIONS = "connections"


PERSONA_DISPLAY_MAP = {
    Persona.MINDZIE: "CTO of mindzie",
    Persona.CENTER_CONSULTING: "President of Consulting",
    Persona.PERSONAL: "Personal",
}

# Email account mapping - defines which tool/account to use for each identifier
# Configure actual email addresses in %LOCALAPPDATA%\cc-director\config\config.json
SEND_FROM_ACCOUNTS = {
    "mindzie": {
        "email": "user@company.com",
        "tool": "cc_outlook",
        "tool_account": None,  # cc_outlook uses default
    },
    "personal": {
        "email": "user@personal.com",
        "tool": "cc_gmail",
        "tool_account": "personal",
    },
    "consulting": {
        "email": "user@consulting.com",
        "tool": "cc_gmail",
        "tool_account": "consulting",
    },
}

# Default account for each persona
PERSONA_DEFAULT_ACCOUNT = {
    Persona.MINDZIE: "mindzie",
    Persona.CENTER_CONSULTING: "consulting",
    Persona.PERSONAL: "personal",
}


class MediaItem(BaseModel):
    """Media attachment."""
    type: str = Field(description="Media type: image, video, document")
    path: str = Field(description="Path to the media file")
    alt_text: Optional[str] = Field(default=None, description="Alt text for accessibility")


class RecipientInfo(BaseModel):
    """Recipient information for messages/emails."""
    name: str
    title: Optional[str] = None
    company: Optional[str] = None
    profile_url: Optional[str] = None


class LinkedInSpecific(BaseModel):
    """LinkedIn-specific fields."""
    visibility: Visibility = Visibility.PUBLIC
    schedule_time: Optional[str] = None


class TwitterSpecific(BaseModel):
    """Twitter/X-specific fields."""
    is_thread: bool = False
    thread_position: Optional[int] = None
    thread_id: Optional[str] = None
    reply_to: Optional[str] = None
    quote_tweet_url: Optional[str] = None


class RedditSpecific(BaseModel):
    """Reddit-specific fields."""
    subreddit: str
    title: Optional[str] = None
    flair: Optional[str] = None
    subreddit_url: Optional[str] = None
    parent_comment: Optional[str] = None


class EmailSpecific(BaseModel):
    """Email-specific fields."""
    to: List[str]
    cc: List[str] = Field(default_factory=list)
    bcc: List[str] = Field(default_factory=list)
    subject: str
    reply_to_message_id: Optional[str] = None
    attachments: List[str] = Field(default_factory=list)


class ArticleSpecific(BaseModel):
    """Article-specific fields."""
    title: str
    subtitle: Optional[str] = None
    target_platforms: List[str] = Field(default_factory=list)
    word_count: Optional[int] = None
    reading_time_minutes: Optional[int] = None
    cover_image: Optional[str] = None
    seo_keywords: List[str] = Field(default_factory=list)


class ContentItem(BaseModel):
    """Base content item that all content types extend."""
    id: str = Field(default_factory=lambda: str(uuid4()))
    ticket_number: Optional[int] = Field(default=None, description="Unique incremental ticket number")
    platform: Platform
    type: ContentType
    persona: Persona = Persona.PERSONAL
    persona_display: Optional[str] = None
    content: str
    created_by: str = "claude_code"
    created_at: str = Field(default_factory=lambda: datetime.now().isoformat())
    status: Status = Status.PENDING_REVIEW
    context_url: Optional[str] = None
    context_title: Optional[str] = None
    context_author: Optional[str] = None
    destination_url: Optional[str] = None
    campaign_id: Optional[str] = None
    tags: List[str] = Field(default_factory=list)
    notes: Optional[str] = None
    media: List[MediaItem] = Field(default_factory=list)
    recipient: Optional[RecipientInfo] = None
    thread_content: Optional[List[str]] = None

    # Platform-specific fields
    linkedin_specific: Optional[LinkedInSpecific] = None
    twitter_specific: Optional[TwitterSpecific] = None
    reddit_specific: Optional[RedditSpecific] = None
    email_specific: Optional[EmailSpecific] = None
    article_specific: Optional[ArticleSpecific] = None

    # Rejection metadata
    rejection_reason: Optional[str] = None
    rejected_at: Optional[str] = None
    rejected_by: Optional[str] = None

    # Posting metadata
    posted_at: Optional[str] = None
    posted_by: Optional[str] = None
    posted_url: Optional[str] = None
    post_id: Optional[str] = None

    # Dispatch fields
    send_timing: SendTiming = SendTiming.ASAP
    scheduled_for: Optional[str] = None  # ISO datetime, only if send_timing == SCHEDULED
    send_from: Optional[str] = None      # Account identifier: "mindzie", "personal", "consulting"

    def model_post_init(self, __context: Any) -> None:
        """Set persona_display and send_from defaults if not provided."""
        if self.persona_display is None:
            self.persona_display = PERSONA_DISPLAY_MAP.get(self.persona, str(self.persona.value))

        # Set default send_from based on persona for emails
        if self.send_from is None and self.platform == Platform.EMAIL:
            self.send_from = PERSONA_DEFAULT_ACCOUNT.get(self.persona, "personal")

    def get_filename(self) -> str:
        """Generate filename for this content item."""
        short_id = self.id[:8]
        return f"{self.platform.value}_{self.type.value}_{short_id}.json"

    def get_send_from_email(self) -> Optional[str]:
        """Get the email address for the send_from account."""
        if self.send_from and self.send_from in SEND_FROM_ACCOUNTS:
            return SEND_FROM_ACCOUNTS[self.send_from]["email"]
        return None

    def get_send_timing_display(self) -> str:
        """Get human-readable send timing description."""
        if self.send_timing == SendTiming.SCHEDULED and self.scheduled_for:
            return f"Scheduled for {self.scheduled_for}"
        elif self.send_timing == SendTiming.HOLD:
            return "On hold (manual dispatch)"
        else:
            return "Immediately when approved"

    def to_json_dict(self) -> Dict[str, Any]:
        """Convert to dictionary for JSON serialization, excluding None values."""
        data = self.model_dump(mode="json")
        # Remove None values recursively
        return _remove_none_values(data)


def _remove_none_values(d: Dict[str, Any]) -> Dict[str, Any]:
    """Recursively remove None values from a dictionary."""
    result = {}
    for k, v in d.items():
        if v is None:
            continue
        elif isinstance(v, dict):
            cleaned = _remove_none_values(v)
            if cleaned:  # Only include non-empty dicts
                result[k] = cleaned
        elif isinstance(v, list):
            if v:  # Only include non-empty lists
                result[k] = [
                    _remove_none_values(item) if isinstance(item, dict) else item
                    for item in v
                    if item is not None
                ]
        else:
            result[k] = v
    return result


class QueueResult(BaseModel):
    """Result from a queue operation."""
    success: bool
    id: Optional[str] = None
    file: Optional[str] = None
    error: Optional[str] = None


class QueueStats(BaseModel):
    """Queue statistics."""
    pending_review: int = 0
    approved: int = 0
    rejected: int = 0
    posted: int = 0
