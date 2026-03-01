"""CSS selectors and XPath for Reddit elements.

These selectors target the new Reddit (www.reddit.com) interface.
Old Reddit (old.reddit.com) selectors are in a separate section.

Note: Reddit's UI changes frequently. These selectors may need updates.
The cc-browser snapshot command with --interactive is the best way to
discover current element refs.
"""

# =============================================================================
# New Reddit (www.reddit.com) - Shreddit Web Components
# =============================================================================

class NewReddit:
    """Selectors for new Reddit interface."""

    # --- Authentication ---
    LOGIN_BUTTON = 'a[href*="login"]'
    LOGGED_IN_USER = 'faceplate-dropdown-menu[name="account"]'
    USERNAME_DISPLAY = 'span[slot="trigger"]'

    # --- Navigation ---
    HOME_FEED = 'main'
    SUBREDDIT_FEED = 'shreddit-feed'

    # --- Post List ---
    POST_CONTAINER = 'shreddit-post'
    POST_TITLE = 'a[slot="title"]'
    POST_AUTHOR = 'a[data-testid="post_author_link"]'
    POST_SUBREDDIT = 'a[data-testid="subreddit-name"]'
    POST_SCORE = 'faceplate-number'
    POST_COMMENTS_LINK = 'a[data-testid="comments-link"]'
    POST_TIME = 'faceplate-timeago'

    # --- Single Post View ---
    POST_CONTENT = 'div[slot="text-body"]'
    POST_MEDIA = 'shreddit-player, shreddit-gallery, img[src*="redd.it"]'

    # --- Comments ---
    COMMENT_TREE = 'shreddit-comment-tree'
    COMMENT = 'shreddit-comment'
    COMMENT_AUTHOR = 'a[data-testid="comment_author_link"]'
    COMMENT_BODY = 'div[slot="comment"]'
    COMMENT_SCORE = 'span[data-testid="comment-upvote-count"]'
    COMMENT_REPLY_BUTTON = 'button[data-testid="comment-reply-button"]'

    # --- Voting ---
    UPVOTE_BUTTON = 'button[upvote]'
    DOWNVOTE_BUTTON = 'button[downvote]'
    UPVOTED_STATE = '[upvoted="true"]'
    DOWNVOTED_STATE = '[downvoted="true"]'

    # --- Actions ---
    SAVE_BUTTON = 'button[data-testid="save-button"]'
    SHARE_BUTTON = 'button[data-testid="share-button"]'
    HIDE_BUTTON = 'button[data-testid="hide-button"]'
    REPORT_BUTTON = 'button[data-testid="report-button"]'

    # --- Post Creation ---
    CREATE_POST_BUTTON = 'a[href*="/submit"]'
    POST_TYPE_TEXT = 'button[aria-label="Text"]'
    POST_TYPE_LINK = 'button[aria-label="Link"]'
    POST_TYPE_IMAGE = 'button[aria-label="Image"]'
    POST_TITLE_INPUT = 'textarea[name="title"]'
    POST_BODY_INPUT = 'div[contenteditable="true"]'
    POST_URL_INPUT = 'input[name="url"]'
    POST_SUBMIT_BUTTON = 'button[type="submit"]'

    # --- Comment Input ---
    COMMENT_INPUT = 'div[data-testid="comment-composer"]'
    COMMENT_SUBMIT = 'button[type="submit"]'

    # --- Subreddit ---
    JOIN_BUTTON = 'button[data-testid="join-button"]'
    JOINED_BUTTON = 'button[data-testid="leave-button"]'
    SUBREDDIT_SIDEBAR = 'shreddit-subreddit-sidebar'
    SUBREDDIT_RULES = 'div[data-testid="rules-content"]'

    # --- User Profile ---
    USER_KARMA = 'span[data-testid="karma"]'
    USER_POSTS_TAB = 'a[href*="/submitted"]'
    USER_COMMENTS_TAB = 'a[href*="/comments"]'

    # --- Inbox ---
    INBOX_UNREAD = 'a[href="/message/unread"]'
    INBOX_MESSAGES = 'a[href="/message/inbox"]'
    MESSAGE_ITEM = 'div[data-testid="message"]'

    # --- Search ---
    SEARCH_INPUT = 'input[type="search"]'
    SEARCH_RESULTS = 'div[data-testid="search-results"]'

    # --- Moderation ---
    MOD_TOOLS = 'button[aria-label="Moderator tools"]'
    MOD_APPROVE = 'button[data-testid="approve-button"]'
    MOD_REMOVE = 'button[data-testid="remove-button"]'
    MOD_SPAM = 'button[data-testid="spam-button"]'
    MOD_LOCK = 'button[data-testid="lock-button"]'
    MOD_DISTINGUISH = 'button[data-testid="distinguish-button"]'
    MOD_STICKY = 'button[data-testid="sticky-button"]'


# =============================================================================
# Old Reddit (old.reddit.com)
# =============================================================================

class OldReddit:
    """Selectors for old Reddit interface."""

    # --- Authentication ---
    LOGIN_FORM = 'form#login-form'
    LOGGED_IN_USER = 'span.user a'
    LOGOUT_LINK = 'a[href*="logout"]'

    # --- Navigation ---
    SUBREDDIT_HEADER = '#header-bottom-left .tabmenu'

    # --- Post List ---
    POST_CONTAINER = 'div.thing.link'
    POST_TITLE = 'a.title'
    POST_AUTHOR = 'a.author'
    POST_SUBREDDIT = 'a.subreddit'
    POST_SCORE = 'div.score.unvoted'
    POST_COMMENTS_LINK = 'a.comments'
    POST_TIME = 'time.live-timestamp'

    # --- Single Post View ---
    POST_CONTENT = 'div.expando'
    SELFTEXT = 'div.usertext-body'

    # --- Comments ---
    COMMENT = 'div.comment'
    COMMENT_AUTHOR = 'a.author'
    COMMENT_BODY = 'div.md'
    COMMENT_SCORE = 'span.score'
    COMMENT_REPLY_BUTTON = 'a.reply-button'

    # --- Voting ---
    UPVOTE_BUTTON = 'div.arrow.up'
    DOWNVOTE_BUTTON = 'div.arrow.down'
    UPVOTED_STATE = 'div.arrow.upmod'
    DOWNVOTED_STATE = 'div.arrow.downmod'

    # --- Actions ---
    SAVE_BUTTON = 'a.save-button'
    HIDE_BUTTON = 'a.hide-button'
    REPORT_BUTTON = 'a.report-button'

    # --- Post Creation ---
    SUBMIT_TEXT = 'a.text-button'
    SUBMIT_LINK = 'a.link-button'
    POST_TITLE_INPUT = 'textarea[name="title"]'
    POST_BODY_INPUT = 'textarea[name="text"]'
    POST_URL_INPUT = 'input[name="url"]'
    POST_SUBMIT_BUTTON = 'button[type="submit"]'

    # --- Comment Input ---
    COMMENT_INPUT = 'textarea[name="text"]'
    COMMENT_SUBMIT = 'button[type="submit"]'

    # --- Subreddit ---
    SUBSCRIBE_BUTTON = 'span.subscribe-button'

    # --- Search ---
    SEARCH_INPUT = 'input[name="q"]'


# =============================================================================
# URL Patterns
# =============================================================================

class RedditURLs:
    """Reddit URL patterns."""

    BASE = "https://www.reddit.com"
    OLD_BASE = "https://old.reddit.com"

    @staticmethod
    def home() -> str:
        return RedditURLs.BASE

    @staticmethod
    def subreddit(name: str, sort: str = "hot") -> str:
        return f"{RedditURLs.BASE}/r/{name}/{sort}"

    @staticmethod
    def post(subreddit: str, post_id: str, slug: str = "") -> str:
        return f"{RedditURLs.BASE}/r/{subreddit}/comments/{post_id}/{slug}"

    @staticmethod
    def post_by_id(post_id: str) -> str:
        # Reddit redirects /comments/ID to full URL
        return f"{RedditURLs.BASE}/comments/{post_id}"

    @staticmethod
    def user(username: str) -> str:
        return f"{RedditURLs.BASE}/user/{username}"

    @staticmethod
    def submit(subreddit: str) -> str:
        return f"{RedditURLs.BASE}/r/{subreddit}/submit"

    @staticmethod
    def inbox() -> str:
        return f"{RedditURLs.BASE}/message/inbox"

    @staticmethod
    def unread() -> str:
        return f"{RedditURLs.BASE}/message/unread"

    @staticmethod
    def compose(to: str = "") -> str:
        if to:
            return f"{RedditURLs.BASE}/message/compose?to={to}"
        return f"{RedditURLs.BASE}/message/compose"

    @staticmethod
    def search(query: str, subreddit: str = "") -> str:
        if subreddit:
            return f"{RedditURLs.BASE}/r/{subreddit}/search?q={query}"
        return f"{RedditURLs.BASE}/search?q={query}"

    @staticmethod
    def saved() -> str:
        return f"{RedditURLs.BASE}/user/me/saved"

    @staticmethod
    def modqueue(subreddit: str) -> str:
        return f"{RedditURLs.BASE}/r/{subreddit}/about/modqueue"
