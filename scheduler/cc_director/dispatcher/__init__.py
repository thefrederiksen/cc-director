"""Communication dispatcher module for cc_director."""

from .config import DispatcherConfig, SEND_FROM_ACCOUNTS
from .dispatcher import CommunicationDispatcher
from .email_sender import EmailSender
from .linkedin_sender import LinkedInSender
from .watcher import ApprovedFolderWatcher
from .sqlite_watcher import SQLiteWatcher, SQLiteDispatcher

__all__ = [
    "DispatcherConfig",
    "SEND_FROM_ACCOUNTS",
    "CommunicationDispatcher",
    "EmailSender",
    "LinkedInSender",
    "ApprovedFolderWatcher",
    "SQLiteWatcher",
    "SQLiteDispatcher",
]
