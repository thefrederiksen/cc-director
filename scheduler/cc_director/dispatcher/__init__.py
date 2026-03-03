"""Communication dispatcher module for cc_director."""

from .config import DispatcherConfig, SEND_FROM_ACCOUNTS, get_send_from_accounts
from .dispatcher import CommunicationDispatcher
from .email_sender import EmailSender
from .linkedin_sender import LinkedInSender
from .reddit_sender import RedditSender
from .twitter_sender import TwitterSender
from .facebook_sender import FacebookSender
from .youtube_sender import YouTubeSender
from .watcher import ApprovedFolderWatcher
from .sqlite_watcher import SQLiteWatcher, SQLiteDispatcher

__all__ = [
    "DispatcherConfig",
    "SEND_FROM_ACCOUNTS",
    "get_send_from_accounts",
    "CommunicationDispatcher",
    "EmailSender",
    "LinkedInSender",
    "RedditSender",
    "TwitterSender",
    "FacebookSender",
    "YouTubeSender",
    "ApprovedFolderWatcher",
    "SQLiteWatcher",
    "SQLiteDispatcher",
]
