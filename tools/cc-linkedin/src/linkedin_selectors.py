"""CSS selectors and URL patterns for LinkedIn elements.

These selectors target the LinkedIn interface.
LinkedIn uses React with various data-testid attributes and ARIA labels.

Note: LinkedIn's UI changes frequently. These selectors may need updates.
The cc-browser snapshot command with --interactive is the best way to
discover current element refs.
"""

from typing import Literal
from urllib.parse import quote


# =============================================================================
# LinkedIn Selectors
# =============================================================================

class LinkedIn:
    """Selectors for LinkedIn interface."""

    # --- Authentication ---
    LOGIN_BUTTON = 'a[data-tracking-control-name="guest_homepage-basic_nav-header-signin"]'
    LOGGED_IN_USER = '.global-nav__me-photo'
    NAV_PROFILE = 'div.feed-identity-module'

    # --- Navigation ---
    HOME_FEED = 'main.scaffold-layout__main'
    NAV_HOME = 'a[href="https://www.linkedin.com/feed/"]'
    NAV_NETWORK = 'a[href="https://www.linkedin.com/mynetwork/"]'
    NAV_JOBS = 'a[href="https://www.linkedin.com/jobs/"]'
    NAV_MESSAGING = 'a[href="https://www.linkedin.com/messaging/"]'
    NAV_NOTIFICATIONS = 'a[href="https://www.linkedin.com/notifications/"]'

    # --- Feed / Posts ---
    FEED_CONTAINER = 'div.feed-shared-update-v2'
    POST_CONTENT = 'div.feed-shared-text'
    POST_AUTHOR = 'span.feed-shared-actor__name'
    POST_AUTHOR_HEADLINE = 'span.feed-shared-actor__description'
    POST_TIME = 'span.feed-shared-actor__sub-description'
    POST_REACTIONS = 'button[aria-label*="reactions"]'
    POST_COMMENTS_COUNT = 'button[aria-label*="comment"]'

    # --- Post Actions ---
    LIKE_BUTTON = 'button[aria-label*="Like"]'
    COMMENT_BUTTON = 'button[aria-label*="Comment"]'
    REPOST_BUTTON = 'button[aria-label*="Repost"]'
    SEND_BUTTON = 'button[aria-label*="Send"]'

    # --- Comment Input ---
    COMMENT_INPUT = 'div.ql-editor[data-placeholder="Add a comment"]'
    COMMENT_SUBMIT = 'button.comments-comment-box__submit-button'

    # --- Profile ---
    PROFILE_CARD = 'div.entity-result__item'
    PROFILE_NAME = 'h1.text-heading-xlarge'
    PROFILE_HEADLINE = 'div.text-body-medium'
    PROFILE_LOCATION = 'span.text-body-small'
    PROFILE_CONNECTIONS = 'span.t-bold'
    PROFILE_ABOUT = 'section.pv-about-section'

    # --- Connection Button ---
    CONNECT_BUTTON = 'button[aria-label*="Connect"]'
    CONNECTED_BUTTON = 'button[aria-label*="Message"]'
    PENDING_BUTTON = 'button[aria-label*="Pending"]'

    # --- Messaging ---
    MESSAGE_THREAD = 'div.msg-conversation-card'
    MESSAGE_INPUT = 'div.msg-form__contenteditable'
    MESSAGE_SEND = 'button.msg-form__send-button'
    MESSAGE_CONTENT = 'div.msg-s-event-listitem__body'

    # --- Search ---
    SEARCH_INPUT = 'input[aria-label="Search"]'
    SEARCH_RESULTS = 'div.search-results-container'
    SEARCH_RESULT_ITEM = 'div.entity-result'

    # --- Connection Requests ---
    INVITATION_CARD = 'li.invitation-card'
    ACCEPT_BUTTON = 'button[aria-label*="Accept"]'
    IGNORE_BUTTON = 'button[aria-label*="Ignore"]'


# =============================================================================
# URL Patterns
# =============================================================================

class LinkedInURLs:
    """LinkedIn URL patterns."""

    BASE = "https://www.linkedin.com"

    @staticmethod
    def home() -> str:
        """Home feed URL."""
        return f"{LinkedInURLs.BASE}/feed"

    @staticmethod
    def profile(username: str) -> str:
        """User profile URL by vanity name."""
        return f"{LinkedInURLs.BASE}/in/{username}"

    @staticmethod
    def post(urn: str) -> str:
        """Post URL by URN or activity ID."""
        # LinkedIn posts use URNs like urn:li:activity:1234567890
        # or just the activity ID
        if urn.startswith("urn:"):
            return f"{LinkedInURLs.BASE}/feed/update/{urn}"
        return f"{LinkedInURLs.BASE}/feed/update/urn:li:activity:{urn}"

    @staticmethod
    def search(
        query: str,
        search_type: Literal["all", "people", "companies", "posts", "jobs", "groups"] = "all",
        network: Literal["", "1st", "2nd", "3rd"] = "",
    ) -> str:
        """Search URL.

        Args:
            query: Search keywords
            search_type: all, people, companies, posts, jobs, groups
            network: Connection degree filter: 1st, 2nd, 3rd (people search only)
        """
        encoded_query = quote(query)
        type_map = {
            "all": "all",
            "people": "people",
            "companies": "companies",
            "posts": "content",
            "jobs": "jobs",
            "groups": "groups",
        }
        result_type = type_map.get(search_type, "all")
        url = f"{LinkedInURLs.BASE}/search/results/{result_type}/?keywords={encoded_query}"

        if network:
            network_map = {"1st": "F", "2nd": "S", "3rd": "O"}
            code = network_map.get(network)
            if code:
                url += f'&network=["{code}"]'

        return url

    @staticmethod
    def messaging() -> str:
        """Messaging inbox URL."""
        return f"{LinkedInURLs.BASE}/messaging"

    @staticmethod
    def messaging_thread(thread_id: str) -> str:
        """Specific messaging thread URL."""
        return f"{LinkedInURLs.BASE}/messaging/thread/{thread_id}"

    @staticmethod
    def connections() -> str:
        """Connections list URL."""
        return f"{LinkedInURLs.BASE}/mynetwork/invite-connect/connections"

    @staticmethod
    def invitations() -> str:
        """Connection invitations URL."""
        return f"{LinkedInURLs.BASE}/mynetwork/invitation-manager"

    @staticmethod
    def network() -> str:
        """My Network page URL."""
        return f"{LinkedInURLs.BASE}/mynetwork"

    @staticmethod
    def notifications() -> str:
        """Notifications page URL."""
        return f"{LinkedInURLs.BASE}/notifications"

    @staticmethod
    def jobs() -> str:
        """Jobs page URL."""
        return f"{LinkedInURLs.BASE}/jobs"

    @staticmethod
    def company(company_id: str) -> str:
        """Company page URL."""
        return f"{LinkedInURLs.BASE}/company/{company_id}"
