"""Configuration for the communication dispatcher."""

from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Optional


# Email account mapping - defines which tool/account to use for each identifier
SEND_FROM_ACCOUNTS: Dict[str, Dict[str, Optional[str]]] = {
    "mindzie": {
        "email": "soren.frederiksen@mindzie.com",
        "tool": "cc-outlook",
        "tool_account": None,  # cc-outlook uses default
    },
    "personal": {
        "email": "soren@duksrevo.com",
        "tool": "cc-gmail",
        "tool_account": "personal",
    },
    "consulting": {
        "email": "soren@centerconsulting.com",
        "tool": "cc-gmail",
        "tool_account": "consulting",
    },
}


@dataclass
class DispatcherConfig:
    """Configuration for the communication dispatcher."""

    # Path to communication manager content folder
    content_path: Path = Path(r"D:\ReposFred\cc-consult\tools\communication_manager\content")

    # Poll interval in seconds (for checking scheduled items)
    poll_interval: float = 30.0

    # Whether dispatcher is enabled
    enabled: bool = True

    @property
    def pending_path(self) -> Path:
        """Path to pending_review folder."""
        return self.content_path / "pending_review"

    @property
    def approved_path(self) -> Path:
        """Path to approved folder."""
        return self.content_path / "approved"

    @property
    def rejected_path(self) -> Path:
        """Path to rejected folder."""
        return self.content_path / "rejected"

    @property
    def posted_path(self) -> Path:
        """Path to posted folder."""
        return self.content_path / "posted"

    def ensure_folders_exist(self) -> None:
        """Ensure all required folders exist."""
        for folder in [
            self.pending_path,
            self.approved_path,
            self.rejected_path,
            self.posted_path,
        ]:
            folder.mkdir(parents=True, exist_ok=True)
