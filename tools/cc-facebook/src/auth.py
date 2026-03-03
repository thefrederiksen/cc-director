"""Authentication and credential management for cc-facebook.

Stores Facebook Page credentials using keyring.
Service name: "cc-facebook"
Keys: app_id, app_secret, page_access_token, page_id
"""

import logging

import keyring

logger = logging.getLogger(__name__)

SERVICE_NAME = "cc-facebook"

# Keyring usernames for each credential field
_FIELDS = ("app_id", "app_secret", "page_access_token", "page_id")


def store_credentials(
    app_id: str,
    app_secret: str,
    page_access_token: str,
    page_id: str,
) -> None:
    """Store all Facebook credentials in keyring.

    Args:
        app_id: Facebook App ID.
        app_secret: Facebook App Secret.
        page_access_token: Long-lived Page Access Token.
        page_id: Facebook Page ID.
    """
    logger.info("[auth] store_credentials: storing credentials for page_id=%s", page_id)
    keyring.set_password(SERVICE_NAME, "app_id", app_id)
    keyring.set_password(SERVICE_NAME, "app_secret", app_secret)
    keyring.set_password(SERVICE_NAME, "page_access_token", page_access_token)
    keyring.set_password(SERVICE_NAME, "page_id", page_id)
    logger.info("[auth] store_credentials: all credentials stored")


def get_credentials() -> dict | None:
    """Retrieve all Facebook credentials from keyring.

    Returns:
        Dict with keys app_id, app_secret, page_access_token, page_id,
        or None if any credential is missing.
    """
    logger.info("[auth] get_credentials: loading credentials from keyring")
    creds = {}
    for field in _FIELDS:
        value = keyring.get_password(SERVICE_NAME, field)
        if value is None:
            logger.info("[auth] get_credentials: missing field=%s", field)
            return None
        creds[field] = value
    logger.info("[auth] get_credentials: all credentials loaded, page_id=%s", creds["page_id"])
    return creds


def delete_credentials() -> bool:
    """Delete all Facebook credentials from keyring.

    Returns:
        True if credentials were deleted, False if none existed.
    """
    logger.info("[auth] delete_credentials: removing credentials from keyring")
    deleted_any = False
    for field in _FIELDS:
        try:
            keyring.delete_password(SERVICE_NAME, field)
            deleted_any = True
        except keyring.errors.PasswordDeleteError:
            pass
    logger.info("[auth] delete_credentials: deleted=%s", deleted_any)
    return deleted_any


def has_credentials() -> bool:
    """Check if all required credentials are stored.

    Returns:
        True if all credentials exist in keyring.
    """
    for field in _FIELDS:
        if keyring.get_password(SERVICE_NAME, field) is None:
            return False
    return True


def get_page_id() -> str | None:
    """Get the configured Page ID from keyring.

    Returns:
        Page ID string, or None if not configured.
    """
    return keyring.get_password(SERVICE_NAME, "page_id")
