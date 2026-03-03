"""Twitter/X authentication via keyring credential storage.

Stores OAuth 1.0a User Context credentials:
- API Key (Consumer Key)
- API Secret (Consumer Secret)
- Access Token
- Access Token Secret
"""

import logging

import keyring

logger = logging.getLogger(__name__)

SERVICE_NAME = "cc-twitter"

_CREDENTIAL_KEYS = ("api_key", "api_secret", "access_token", "access_token_secret")


def store_credentials(
    api_key: str,
    api_secret: str,
    access_token: str,
    access_token_secret: str,
) -> None:
    """Store all four OAuth 1.0a credentials in keyring.

    Args:
        api_key: Twitter API Key (Consumer Key).
        api_secret: Twitter API Secret (Consumer Secret).
        access_token: OAuth Access Token.
        access_token_secret: OAuth Access Token Secret.
    """
    logger.info("[auth] store_credentials: storing 4 credential values")
    keyring.set_password(SERVICE_NAME, "api_key", api_key)
    keyring.set_password(SERVICE_NAME, "api_secret", api_secret)
    keyring.set_password(SERVICE_NAME, "access_token", access_token)
    keyring.set_password(SERVICE_NAME, "access_token_secret", access_token_secret)
    logger.info("[auth] store_credentials: done")


def get_credentials() -> dict | None:
    """Retrieve all four credentials from keyring.

    Returns:
        Dict with keys api_key, api_secret, access_token, access_token_secret,
        or None if any credential is missing.
    """
    logger.info("[auth] get_credentials: retrieving credentials")
    creds = {}
    for key in _CREDENTIAL_KEYS:
        value = keyring.get_password(SERVICE_NAME, key)
        if value is None:
            logger.info("[auth] get_credentials: missing key=%s", key)
            return None
        creds[key] = value
    logger.info("[auth] get_credentials: all credentials found")
    return creds


def delete_credentials() -> bool:
    """Delete all stored credentials from keyring.

    Returns:
        True if credentials were deleted, False if none were found.
    """
    logger.info("[auth] delete_credentials: removing credentials")
    found_any = False
    for key in _CREDENTIAL_KEYS:
        try:
            existing = keyring.get_password(SERVICE_NAME, key)
            if existing is not None:
                keyring.delete_password(SERVICE_NAME, key)
                found_any = True
        except keyring.errors.PasswordDeleteError:
            pass
    logger.info("[auth] delete_credentials: found_any=%s", found_any)
    return found_any


def has_credentials() -> bool:
    """Check whether all four credentials are stored.

    Returns:
        True if all credentials exist in keyring.
    """
    return get_credentials() is not None
