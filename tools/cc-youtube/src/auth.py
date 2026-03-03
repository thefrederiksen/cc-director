"""Authentication for YouTube via OAuth (YouTube Data API v3).

OAuth Setup:
  - Requires Google Cloud project with YouTube Data API v3 enabled
  - Download OAuth credentials as credentials.json
  - Place in config/youtube/credentials.json
  - Run: cc-youtube auth
"""

import json
import logging
from pathlib import Path
from typing import Optional

from google.auth.transport.requests import Request
from google.auth.exceptions import RefreshError
from google.oauth2.credentials import Credentials
from google_auth_oauthlib.flow import InstalledAppFlow

import keyring

try:
    from cc_storage import CcStorage
except ImportError:
    import sys
    _tools_dir = str(Path(__file__).resolve().parent.parent.parent)
    if _tools_dir not in sys.path:
        sys.path.insert(0, _tools_dir)
    from cc_storage import CcStorage

logger = logging.getLogger(__name__)

# YouTube API scopes
SCOPES = [
    "https://www.googleapis.com/auth/youtube",
    "https://www.googleapis.com/auth/youtube.upload",
    "https://www.googleapis.com/auth/youtube.force-ssl",
]

# Keyring service name
KEYRING_SERVICE = "cc-youtube"

# Config directory - uses centralized cc-director storage
CONFIG_DIR = CcStorage.tool_config("youtube")
CREDENTIALS_PATH = CONFIG_DIR / "credentials.json"
TOKEN_PATH = CONFIG_DIR / "token.json"


def get_config_dir() -> Path:
    """Get the configuration directory, creating it if necessary."""
    CONFIG_DIR.mkdir(parents=True, exist_ok=True)
    return CONFIG_DIR


def get_credentials_path() -> Path:
    """Get the path to the OAuth credentials file."""
    return CREDENTIALS_PATH


def get_token_path() -> Path:
    """Get the path to the token file."""
    return TOKEN_PATH


def credentials_exist() -> bool:
    """Check if OAuth credentials file exists."""
    return CREDENTIALS_PATH.exists()


def token_exists() -> bool:
    """Check if token file exists."""
    return TOKEN_PATH.exists()


def load_credentials() -> Optional[Credentials]:
    """Load OAuth credentials from token file if available and valid."""
    if not TOKEN_PATH.exists():
        return None

    creds = Credentials.from_authorized_user_file(str(TOKEN_PATH), SCOPES)

    # If credentials are expired, try to refresh
    if creds and creds.expired and creds.refresh_token:
        try:
            creds.refresh(Request())
            save_credentials(creds)
        except RefreshError as e:
            logger.warning("Token refresh failed: %s", e)
            return None

    return creds if creds and creds.valid else None


def save_credentials(creds: Credentials) -> None:
    """Save OAuth credentials to token file."""
    get_config_dir()  # Ensure directory exists
    TOKEN_PATH.write_text(creds.to_json())


def authenticate(force: bool = False, open_browser: bool = True) -> Credentials:
    """Authenticate with YouTube Data API v3 via OAuth.

    Args:
        force: If True, force re-authentication even if valid token exists.
        open_browser: If True, auto-open default browser. If False, print the
            auth URL so the user can open it in a specific browser.

    Returns:
        Valid credentials for YouTube API.

    Raises:
        FileNotFoundError: If credentials.json is missing.
    """
    if not CREDENTIALS_PATH.exists():
        raise FileNotFoundError(
            "OAuth credentials not found.\n\n"
            f"Expected location: {CREDENTIALS_PATH}\n\n"
            "Setup instructions:\n"
            "  1. Go to https://console.cloud.google.com/\n"
            "  2. Create a project (or select existing)\n"
            "  3. Enable 'YouTube Data API v3'\n"
            "  4. Go to Credentials -> Create Credentials -> OAuth client ID\n"
            "  5. Application type: Desktop app\n"
            "  6. Download the JSON and save as:\n"
            f"     {CREDENTIALS_PATH}"
        )

    creds = None

    # Try to load existing credentials if not forcing re-auth
    if not force:
        creds = load_credentials()

    # If no valid credentials, run OAuth flow
    if not creds:
        flow = InstalledAppFlow.from_client_secrets_file(str(CREDENTIALS_PATH), SCOPES)
        creds = flow.run_local_server(port=0, open_browser=open_browser)
        save_credentials(creds)

    return creds


def revoke_token() -> bool:
    """Delete the token file to force re-authentication."""
    if TOKEN_PATH.exists():
        TOKEN_PATH.unlink()
        return True
    return False


def get_auth_status() -> dict:
    """Get the authentication status.

    Returns:
        Dict with config_dir, credentials_exists, token_exists, authenticated.
    """
    status = {
        "config_dir": str(get_config_dir()),
        "credentials_exists": credentials_exist(),
        "token_exists": token_exists(),
        "authenticated": False,
    }

    if status["credentials_exists"] and status["token_exists"]:
        creds = load_credentials()
        if creds and creds.valid:
            status["authenticated"] = True

    return status
