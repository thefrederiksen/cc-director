"""Twitter/X API wrapper using tweepy for API v2 with OAuth 1.0a User Context."""

import logging
import re
from typing import Optional

import tweepy

logger = logging.getLogger(__name__)

try:
    from .auth import get_credentials
except ImportError:
    from src.auth import get_credentials


def extract_tweet_id(url_or_id: str) -> str:
    """Extract tweet ID from a URL or return as-is if already an ID.

    Supports URLs like:
        https://twitter.com/user/status/123456789
        https://x.com/user/status/123456789
        https://x.com/user/status/123456789?s=20

    Args:
        url_or_id: A tweet URL or numeric tweet ID string.

    Returns:
        The numeric tweet ID as a string.

    Raises:
        ValueError: If the input is not a valid tweet URL or ID.
    """
    logger.info("[extract_tweet_id] input=%s", url_or_id)

    # If it is already a numeric ID
    if url_or_id.strip().isdigit():
        return url_or_id.strip()

    # Try to parse from URL
    pattern = r"(?:twitter\.com|x\.com)/\w+/status/(\d+)"
    match = re.search(pattern, url_or_id)
    if match:
        tweet_id = match.group(1)
        logger.info("[extract_tweet_id] extracted id=%s", tweet_id)
        return tweet_id

    raise ValueError(
        f"Cannot extract tweet ID from: {url_or_id}\n"
        "Expected a numeric ID or URL like https://x.com/user/status/123456789"
    )


class TwitterAPI:
    """Twitter API v2 client wrapper using tweepy."""

    def __init__(self) -> None:
        """Initialize Twitter client from stored credentials.

        Raises:
            RuntimeError: If no credentials are stored.
        """
        logger.info("[TwitterAPI] __init__: loading credentials")
        creds = get_credentials()
        if creds is None:
            raise RuntimeError(
                "No Twitter credentials found. Run 'cc-twitter auth' to configure."
            )

        # API v2 client (for tweets, likes, retweets, user info)
        self.client = tweepy.Client(
            consumer_key=creds["api_key"],
            consumer_secret=creds["api_secret"],
            access_token=creds["access_token"],
            access_token_secret=creds["access_token_secret"],
        )

        # API v1.1 auth (for media upload, which v2 does not support)
        auth = tweepy.OAuth1UserHandler(
            consumer_key=creds["api_key"],
            consumer_secret=creds["api_secret"],
            access_token=creds["access_token"],
            access_token_secret=creds["access_token_secret"],
        )
        self.api_v1 = tweepy.API(auth)

        logger.info("[TwitterAPI] __init__: clients created")

    def get_me(self) -> dict:
        """Get authenticated user info.

        Returns:
            Dict with id, name, username, description, public_metrics.
        """
        logger.info("[TwitterAPI] get_me: fetching user info")
        response = self.client.get_me(
            user_fields=["id", "name", "username", "description", "public_metrics"]
        )
        user = response.data
        result = {
            "id": str(user.id),
            "name": user.name,
            "username": user.username,
            "description": user.description or "",
            "public_metrics": dict(user.public_metrics) if user.public_metrics else {},
        }
        logger.info("[TwitterAPI] get_me: username=%s", result["username"])
        return result

    def post(self, text: str) -> dict:
        """Create a new tweet.

        Args:
            text: Tweet text content (max 280 characters).

        Returns:
            Dict with id and url of the created tweet.
        """
        logger.info("[TwitterAPI] post: text_len=%d", len(text))
        response = self.client.create_tweet(text=text)
        tweet_id = str(response.data["id"])
        me = self.get_me()
        url = f"https://x.com/{me['username']}/status/{tweet_id}"
        result = {"id": tweet_id, "url": url}
        logger.info("[TwitterAPI] post: created id=%s", tweet_id)
        return result

    def reply(self, text: str, reply_to_id: str) -> dict:
        """Reply to a tweet.

        Args:
            text: Reply text content.
            reply_to_id: Tweet ID to reply to.

        Returns:
            Dict with id and url of the reply tweet.
        """
        logger.info("[TwitterAPI] reply: reply_to=%s text_len=%d", reply_to_id, len(text))
        response = self.client.create_tweet(
            text=text,
            in_reply_to_tweet_id=reply_to_id,
        )
        tweet_id = str(response.data["id"])
        me = self.get_me()
        url = f"https://x.com/{me['username']}/status/{tweet_id}"
        result = {"id": tweet_id, "url": url}
        logger.info("[TwitterAPI] reply: created id=%s", tweet_id)
        return result

    def thread(self, texts: list[str]) -> list[dict]:
        """Post a thread (chain of tweets replying to each other).

        Args:
            texts: List of tweet texts forming the thread.

        Returns:
            List of dicts, each with id and url.

        Raises:
            ValueError: If texts list is empty.
        """
        if not texts:
            raise ValueError("Thread must contain at least one tweet")

        logger.info("[TwitterAPI] thread: posting %d tweets", len(texts))
        results = []

        # Post the first tweet
        first = self.post(texts[0])
        results.append(first)

        # Chain replies
        previous_id = first["id"]
        for text in texts[1:]:
            tweet = self.reply(text, previous_id)
            results.append(tweet)
            previous_id = tweet["id"]

        logger.info("[TwitterAPI] thread: posted %d tweets", len(results))
        return results

    def like(self, tweet_id: str) -> bool:
        """Like a tweet.

        Args:
            tweet_id: ID of the tweet to like.

        Returns:
            True if the tweet was liked successfully.
        """
        logger.info("[TwitterAPI] like: tweet_id=%s", tweet_id)
        response = self.client.like(tweet_id)
        liked = response.data.get("liked", False)
        logger.info("[TwitterAPI] like: result=%s", liked)
        return liked

    def retweet(self, tweet_id: str) -> bool:
        """Retweet a tweet.

        Args:
            tweet_id: ID of the tweet to retweet.

        Returns:
            True if the tweet was retweeted successfully.
        """
        logger.info("[TwitterAPI] retweet: tweet_id=%s", tweet_id)
        response = self.client.retweet(tweet_id)
        retweeted = response.data.get("retweeted", False)
        logger.info("[TwitterAPI] retweet: result=%s", retweeted)
        return retweeted

    def delete(self, tweet_id: str) -> bool:
        """Delete a tweet.

        Args:
            tweet_id: ID of the tweet to delete.

        Returns:
            True if the tweet was deleted successfully.
        """
        logger.info("[TwitterAPI] delete: tweet_id=%s", tweet_id)
        response = self.client.delete_tweet(tweet_id)
        deleted = response.data.get("deleted", False)
        logger.info("[TwitterAPI] delete: result=%s", deleted)
        return deleted

    def timeline(self, count: int = 10) -> list[dict]:
        """Get the authenticated user's home timeline (reverse chronological).

        Args:
            count: Number of tweets to retrieve (max 100).

        Returns:
            List of tweet dicts with id, text, author_id, created_at.
        """
        logger.info("[TwitterAPI] timeline: count=%d", count)
        response = self.client.get_home_timeline(
            max_results=min(count, 100),
            tweet_fields=["id", "text", "author_id", "created_at"],
        )
        tweets = []
        if response.data:
            for tweet in response.data:
                tweets.append({
                    "id": str(tweet.id),
                    "text": tweet.text,
                    "author_id": str(tweet.author_id),
                    "created_at": str(tweet.created_at) if tweet.created_at else "",
                })
        logger.info("[TwitterAPI] timeline: returned %d tweets", len(tweets))
        return tweets

    def mentions(self, count: int = 10) -> list[dict]:
        """Get recent mentions of the authenticated user.

        Args:
            count: Number of mentions to retrieve (max 100).

        Returns:
            List of tweet dicts with id, text, author_id, created_at.
        """
        logger.info("[TwitterAPI] mentions: count=%d", count)
        me = self.get_me()
        response = self.client.get_users_mentions(
            id=me["id"],
            max_results=min(count, 100),
            tweet_fields=["id", "text", "author_id", "created_at"],
        )
        tweets = []
        if response.data:
            for tweet in response.data:
                tweets.append({
                    "id": str(tweet.id),
                    "text": tweet.text,
                    "author_id": str(tweet.author_id),
                    "created_at": str(tweet.created_at) if tweet.created_at else "",
                })
        logger.info("[TwitterAPI] mentions: returned %d tweets", len(tweets))
        return tweets
