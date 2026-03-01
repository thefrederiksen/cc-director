"""Pydantic models for LinkedIn data structures."""

from typing import Optional, List
from datetime import datetime
from pydantic import BaseModel


class LinkedInUser(BaseModel):
    """LinkedIn user information."""
    username: str  # vanity URL slug (e.g., "johndoe")
    name: str
    headline: Optional[str] = None
    location: Optional[str] = None
    connections: Optional[int] = None
    profile_url: Optional[str] = None


class Post(BaseModel):
    """LinkedIn post/update."""
    id: str  # activity ID or URN
    author: str  # author name
    author_headline: Optional[str] = None
    content: str
    likes: int = 0
    comments: int = 0
    shares: int = 0
    time_ago: str = ""
    url: Optional[str] = None


class Connection(BaseModel):
    """LinkedIn connection."""
    username: str  # vanity URL slug
    name: str
    headline: Optional[str] = None
    connected_date: Optional[str] = None


class Message(BaseModel):
    """LinkedIn message."""
    id: str
    sender: str
    content: str
    time_ago: str = ""
    is_read: bool = True


class MessageThread(BaseModel):
    """LinkedIn messaging thread."""
    id: str
    participants: List[str]
    last_message: Optional[str] = None
    last_message_time: Optional[str] = None
    unread: bool = False


class SearchResult(BaseModel):
    """LinkedIn search result."""
    name: str
    headline: Optional[str] = None
    location: Optional[str] = None
    url: Optional[str] = None
    result_type: str = "person"  # person, company, post, job, group


class Invitation(BaseModel):
    """LinkedIn connection invitation."""
    id: str
    name: str
    headline: Optional[str] = None
    time_ago: str = ""
    mutual_connections: Optional[int] = None


class FeedItem(BaseModel):
    """Item in LinkedIn feed."""
    id: str
    author: str
    author_headline: Optional[str] = None
    content: str
    likes: int = 0
    comments: int = 0
    time_ago: str = ""
    is_repost: bool = False
    original_author: Optional[str] = None
