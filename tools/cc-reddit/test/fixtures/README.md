# Test Fixtures: cc-reddit

## Overview
Mock Reddit response structures for testing parse and display logic offline.
Like cc-linkedin, cc-reddit uses live browser automation with human-like delays,
so fixtures provide representative data structures rather than live interactions.

## Fixtures
- `post_text.json` - Text post with title, body, author, score, and comments
- `post_link.json` - Link post with URL, thumbnail metadata, and comments
- `post_media.json` - Media post with embedded video/image metadata
- `comment_thread.json` - Nested comment tree with replies and score data
- `user_profile.json` - User profile response (karma, cake day, about)
- `subreddit_feed.json` - Subreddit listing with multiple posts and pagination
- `search_results.json` - Search endpoint response with matched posts
- `saved_posts.json` - Saved posts listing for the authenticated user

## Notes
- NEVER use cc-browser directly for Reddit -- always use cc-reddit
- cc-reddit has built-in human-like delays and random jitter to avoid detection
- All personal data in fixtures must be fictional (usernames, post content)
- Comment trees can be deeply nested; test rendering at various depths
- Score and vote counts are integers; karma is broken into post/comment
- Fixtures represent the parsed data structures cc-reddit returns, not raw HTML
- Test display formatting (truncation, indentation) with these structures
- Pagination tokens (after/before) should be included for feed testing

## Last Validated
Date: 2026-02-26
