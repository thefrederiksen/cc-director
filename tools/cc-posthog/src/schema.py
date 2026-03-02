"""Pydantic models for PostHog API responses."""

from typing import Any, Optional
from pydantic import BaseModel


class PageViewRow(BaseModel):
    """A single row from the page views query."""
    page: str
    views: int
    unique_visitors: int


class TrafficSource(BaseModel):
    """A single traffic source with count."""
    source: str
    count: int
    percentage: float = 0.0


class VisitorCount(BaseModel):
    """Daily visitor count."""
    date: str
    visitors: int


class FunnelStep(BaseModel):
    """A single step in a conversion funnel."""
    step: str
    event: str
    count: int
    conversion_rate: float
    drop_off: int
    drop_off_rate: float


class EventRecord(BaseModel):
    """A single event from the events log."""
    timestamp: str
    event: str
    person: str
    url: str
    properties: dict[str, Any] = {}


class EventCount(BaseModel):
    """Event name with total count."""
    event: str
    count: int


class Recording(BaseModel):
    """Session recording summary."""
    id: str
    start_time: str
    duration_seconds: int
    pages_visited: int
    click_count: int
    person: str = ""


class RecordingEvent(BaseModel):
    """A single event within a session recording."""
    timestamp: str
    event: str
    properties: dict[str, Any] = {}


class StatusInfo(BaseModel):
    """PostHog project status information."""
    project_name: str
    project_id: int
    host: str
    event_count: Optional[int] = None
    recording_count: Optional[int] = None


class AnalyticsReport(BaseModel):
    """Comprehensive analytics report combining multiple queries."""
    project: str
    period: str
    status: Optional[StatusInfo] = None
    views: list[PageViewRow] = []
    sources: list[TrafficSource] = []
    visitors: list[VisitorCount] = []
    funnel: list[FunnelStep] = []
    event_counts: list[EventCount] = []
