"""Pydantic models for Reddit data structures."""

from typing import Optional, List
from datetime import datetime
from pydantic import BaseModel


class RedditUser(BaseModel):
    """Reddit user information."""
    username: str
    karma: Optional[int] = None
    cake_day: Optional[str] = None
    is_mod: bool = False


class Post(BaseModel):
    """Reddit post."""
    id: str
    title: str
    author: str
    subreddit: str
    url: Optional[str] = None
    selftext: Optional[str] = None
    score: int = 0
    upvote_ratio: Optional[float] = None
    num_comments: int = 0
    created_utc: Optional[datetime] = None
    is_self: bool = True
    permalink: str = ""
    flair: Optional[str] = None
    is_nsfw: bool = False
    is_spoiler: bool = False
    is_locked: bool = False
    is_stickied: bool = False


class Comment(BaseModel):
    """Reddit comment."""
    id: str
    author: str
    body: str
    score: int = 0
    created_utc: Optional[datetime] = None
    permalink: str = ""
    parent_id: Optional[str] = None
    is_op: bool = False
    is_stickied: bool = False
    depth: int = 0


class Subreddit(BaseModel):
    """Subreddit information."""
    name: str
    title: str
    description: Optional[str] = None
    subscribers: Optional[int] = None
    created_utc: Optional[datetime] = None
    is_nsfw: bool = False
    rules: Optional[List[str]] = None


class Message(BaseModel):
    """Reddit direct message."""
    id: str
    author: str
    subject: str
    body: str
    created_utc: Optional[datetime] = None
    is_read: bool = False


class FeedItem(BaseModel):
    """Item in a subreddit feed."""
    id: str
    title: str
    author: str
    score: int = 0
    num_comments: int = 0
    time_ago: str = ""
    permalink: str = ""
    thumbnail: Optional[str] = None
    flair: Optional[str] = None
