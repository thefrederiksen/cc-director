"""Facebook Graph API wrapper using httpx.

All interactions with the Facebook Graph API v19.0 go through this module.
Uses httpx for HTTP requests -- no heavy SDK required.
"""

import logging
import re
from typing import Optional
from urllib.parse import urlparse, parse_qs

import httpx

try:
    from .auth import get_credentials
except ImportError:
    from src.auth import get_credentials

logger = logging.getLogger(__name__)

BASE_URL = "https://graph.facebook.com/v19.0"


def extract_post_id(url_or_id: str) -> str:
    """Extract a Facebook post ID from a URL or return the ID as-is.

    Handles these URL patterns:
      - https://www.facebook.com/{page}/posts/{post_id}
      - https://www.facebook.com/permalink.php?story_fbid={id}&id={page_id}
      - https://www.facebook.com/{page_id}/posts/{post_id}
      - Plain ID like "123456789_987654321" or "123456789"

    Args:
        url_or_id: A Facebook URL or post ID string.

    Returns:
        The extracted post ID.
    """
    logger.info("[facebook_api] extract_post_id: input=%s", url_or_id)

    # If it looks like a plain ID (digits, underscores), return as-is
    if re.match(r'^[\d_]+$', url_or_id):
        logger.info("[facebook_api] extract_post_id: plain ID detected")
        return url_or_id

    # Try to parse as URL
    parsed = urlparse(url_or_id)
    if not parsed.scheme:
        # Not a URL, return as-is
        return url_or_id

    path = parsed.path.strip("/")

    # Pattern: /permalink.php?story_fbid=XXX&id=YYY
    if "permalink.php" in path:
        params = parse_qs(parsed.query)
        story_fbid = params.get("story_fbid", [None])[0]
        page_id = params.get("id", [None])[0]
        if story_fbid and page_id:
            result = f"{page_id}_{story_fbid}"
            logger.info("[facebook_api] extract_post_id: permalink -> %s", result)
            return result
        if story_fbid:
            return story_fbid

    # Pattern: /{page_name_or_id}/posts/{post_id}
    match = re.search(r'/posts/(\d+)', path)
    if match:
        post_id = match.group(1)
        # Try to get page_id from the path prefix
        parts = path.split("/posts/")
        if parts[0]:
            page_part = parts[0].split("/")[-1]
            if page_part.isdigit():
                result = f"{page_part}_{post_id}"
                logger.info("[facebook_api] extract_post_id: posts URL -> %s", result)
                return result
        logger.info("[facebook_api] extract_post_id: posts URL (post_id only) -> %s", post_id)
        return post_id

    # Pattern: /{page_id}/posts/{pfbid...} (new-style post IDs)
    match = re.search(r'/posts/(pfbid\w+)', path)
    if match:
        result = match.group(1)
        logger.info("[facebook_api] extract_post_id: pfbid URL -> %s", result)
        return result

    # Could not parse, return as-is
    logger.info("[facebook_api] extract_post_id: could not parse, returning as-is")
    return url_or_id


class FacebookAPI:
    """Facebook Graph API v19.0 client.

    Uses httpx for all HTTP communication. Credentials are loaded from
    keyring via the auth module on initialization.
    """

    def __init__(self) -> None:
        """Initialize the Facebook API client.

        Loads credentials from keyring. Raises RuntimeError if credentials
        are not configured.
        """
        logger.info("[FacebookAPI] __init__: loading credentials")
        creds = get_credentials()
        if creds is None:
            raise RuntimeError(
                "Facebook credentials not configured. "
                "Run 'cc-facebook auth' to set up credentials."
            )

        self._access_token: str = creds["page_access_token"]
        self._page_id: str = creds["page_id"]
        self._app_id: str = creds["app_id"]
        self._app_secret: str = creds["app_secret"]
        self._base_url: str = BASE_URL
        self._client = httpx.Client(timeout=30.0)
        logger.info("[FacebookAPI] __init__: ready, page_id=%s", self._page_id)

    def _params(self, **extra: str) -> dict:
        """Build query parameters with access token included.

        Args:
            **extra: Additional query parameters.

        Returns:
            Dict of query parameters.
        """
        params = {"access_token": self._access_token}
        params.update(extra)
        return params

    def _url(self, endpoint: str) -> str:
        """Build full API URL for an endpoint.

        Args:
            endpoint: API endpoint path (without base URL or leading slash).

        Returns:
            Full URL string.
        """
        return f"{self._base_url}/{endpoint.lstrip('/')}"

    def _check_response(self, response: httpx.Response, operation: str) -> dict:
        """Check API response and raise on error.

        Args:
            response: The httpx Response object.
            operation: Description of the operation for error messages.

        Returns:
            Parsed JSON response body.

        Raises:
            RuntimeError: If the API returned an error.
        """
        data = response.json()
        if "error" in data:
            error = data["error"]
            msg = error.get("message", "Unknown error")
            code = error.get("code", "N/A")
            err_type = error.get("type", "N/A")
            raise RuntimeError(
                f"Facebook API error during {operation}: "
                f"[{code}] {err_type} - {msg}"
            )
        return data

    def post(self, message: str, link: str = "") -> dict:
        """Create a post on the configured Facebook Page.

        Args:
            message: The post text content.
            link: Optional URL to attach to the post.

        Returns:
            Dict with 'id' (post ID) and 'url' (permalink).
        """
        logger.info("[FacebookAPI] post: creating page post, has_link=%s", bool(link))
        payload = {"message": message}
        if link:
            payload["link"] = link

        response = self._client.post(
            self._url(f"{self._page_id}/feed"),
            params=self._params(),
            data=payload,
        )
        data = self._check_response(response, "create post")
        post_id = data.get("id", "")
        url = f"https://www.facebook.com/{post_id}" if post_id else ""
        logger.info("[FacebookAPI] post: created post_id=%s", post_id)
        return {"id": post_id, "url": url}

    def comment(self, post_id: str, message: str) -> dict:
        """Add a comment to a post.

        Args:
            post_id: The post ID to comment on.
            message: The comment text.

        Returns:
            Dict with 'id' (comment ID).
        """
        logger.info("[FacebookAPI] comment: post_id=%s", post_id)
        response = self._client.post(
            self._url(f"{post_id}/comments"),
            params=self._params(),
            data={"message": message},
        )
        data = self._check_response(response, "create comment")
        comment_id = data.get("id", "")
        logger.info("[FacebookAPI] comment: created comment_id=%s", comment_id)
        return {"id": comment_id}

    def reply(self, comment_id: str, message: str) -> dict:
        """Reply to a comment.

        Args:
            comment_id: The comment ID to reply to.
            message: The reply text.

        Returns:
            Dict with 'id' (reply comment ID).
        """
        logger.info("[FacebookAPI] reply: comment_id=%s", comment_id)
        response = self._client.post(
            self._url(f"{comment_id}/comments"),
            params=self._params(),
            data={"message": message},
        )
        data = self._check_response(response, "reply to comment")
        reply_id = data.get("id", "")
        logger.info("[FacebookAPI] reply: created reply_id=%s", reply_id)
        return {"id": reply_id}

    def delete_post(self, post_id: str) -> bool:
        """Delete a post.

        Args:
            post_id: The post ID to delete.

        Returns:
            True if the post was deleted successfully.
        """
        logger.info("[FacebookAPI] delete_post: post_id=%s", post_id)
        response = self._client.delete(
            self._url(post_id),
            params=self._params(),
        )
        data = self._check_response(response, "delete post")
        success = data.get("success", False)
        logger.info("[FacebookAPI] delete_post: success=%s", success)
        return bool(success)

    def list_posts(self, count: int = 10) -> list[dict]:
        """List recent posts from the configured page.

        Args:
            count: Number of posts to retrieve (default 10).

        Returns:
            List of post dicts with keys: id, message, created_time, permalink_url.
        """
        logger.info("[FacebookAPI] list_posts: count=%d", count)
        fields = "id,message,created_time,permalink_url,shares,type"
        response = self._client.get(
            self._url(f"{self._page_id}/posts"),
            params=self._params(limit=str(count), fields=fields),
        )
        data = self._check_response(response, "list posts")
        posts = data.get("data", [])
        logger.info("[FacebookAPI] list_posts: returned %d posts", len(posts))
        return posts

    def get_pages(self) -> list[dict]:
        """List Facebook Pages managed by the authenticated user.

        Returns:
            List of page dicts with keys: id, name, access_token, category.
        """
        logger.info("[FacebookAPI] get_pages: fetching managed pages")
        response = self._client.get(
            self._url("me/accounts"),
            params=self._params(fields="id,name,access_token,category"),
        )
        data = self._check_response(response, "list pages")
        pages = data.get("data", [])
        logger.info("[FacebookAPI] get_pages: returned %d pages", len(pages))
        return pages

    def get_page_info(self) -> dict:
        """Get info about the configured page.

        Returns:
            Dict with page details: id, name, category, fan_count,
            followers_count, link, about.
        """
        logger.info("[FacebookAPI] get_page_info: page_id=%s", self._page_id)
        fields = "id,name,category,fan_count,followers_count,link,about,website"
        response = self._client.get(
            self._url(self._page_id),
            params=self._params(fields=fields),
        )
        data = self._check_response(response, "get page info")
        logger.info("[FacebookAPI] get_page_info: name=%s", data.get("name", "N/A"))
        return data

    def get_me(self) -> dict:
        """Get info about the authenticated user/page.

        Returns:
            Dict with user/page identity info.
        """
        logger.info("[FacebookAPI] get_me: fetching identity")
        response = self._client.get(
            self._url("me"),
            params=self._params(fields="id,name"),
        )
        data = self._check_response(response, "get me")
        logger.info("[FacebookAPI] get_me: id=%s, name=%s", data.get("id"), data.get("name"))
        return data
