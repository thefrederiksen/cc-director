# Communication Manager Content Schema

This document defines the JSON format for all content types that can be reviewed and approved through the Communication Manager.

## Directory Structure

```
content/
  pending_review/    # New content awaiting review
  approved/          # Approved, ready for posting
  rejected/          # Rejected content with reasons
  posted/            # Successfully posted content
```

## Base Schema (All Content Types)

Every content file must include these base fields:

```json
{
  "id": "unique-guid",
  "platform": "linkedin | twitter | reddit | youtube | email | blog",
  "type": "post | comment | reply | message | article | email",
  "persona": "mindzie | consulting | personal",
  "persona_display": "CTO of mindzie | Consulting persona | Personal",
  "content": "The actual text content",
  "created_by": "agent_name or tool_name",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review | approved | rejected | posted",
  "context_url": "https://... (optional - link to what we're responding to)",
  "destination_url": "https://... (optional - where this will be posted)",
  "campaign_id": "optional-campaign-reference",
  "tags": ["optional", "tags", "for", "filtering"],
  "notes": "Optional notes for reviewer"
}
```

## Platform-Specific Schemas

### LinkedIn Post

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440001",
  "platform": "linkedin",
  "type": "post",
  "persona": "mindzie",
  "persona_display": "CTO of mindzie",
  "content": "Excited to announce our new process mining capabilities...\n\nKey highlights:\n- Feature 1\n- Feature 2\n\n#ProcessMining #mindzie",
  "created_by": "claude_code",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review",
  "destination_url": "https://www.linkedin.com/in/username/",
  "media": [
    {
      "type": "image",
      "path": "content/media/feature_screenshot.png",
      "alt_text": "Screenshot of new feature"
    }
  ],
  "linkedin_specific": {
    "visibility": "public | connections",
    "schedule_time": "2024-02-22T09:00:00Z"
  }
}
```

### LinkedIn Comment

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440002",
  "platform": "linkedin",
  "type": "comment",
  "persona": "personal",
  "persona_display": "Personal",
  "content": "Great insights! We've seen similar patterns at mindzie...",
  "created_by": "engagement_agent",
  "created_at": "2024-02-21T11:00:00Z",
  "status": "pending_review",
  "context_url": "https://www.linkedin.com/posts/someuser_activity-123456",
  "context_title": "Original post about process mining trends",
  "context_author": "John Doe, CEO at SomeCompany"
}
```

### LinkedIn Message (DM)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440003",
  "platform": "linkedin",
  "type": "message",
  "persona": "mindzie",
  "persona_display": "CTO of mindzie",
  "content": "Hi Sarah, thanks for connecting! I noticed you're working on process improvement...",
  "created_by": "outreach_agent",
  "created_at": "2024-02-21T12:00:00Z",
  "status": "pending_review",
  "recipient": {
    "name": "Sarah Johnson",
    "title": "VP Operations at TechCorp",
    "profile_url": "https://www.linkedin.com/in/sarahjohnson/"
  }
}
```

### Twitter/X Tweet

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440010",
  "platform": "twitter",
  "type": "post",
  "persona": "mindzie",
  "persona_display": "CTO of mindzie",
  "content": "Process mining isn't just about finding inefficiencies - it's about understanding your business at a deeper level.\n\nHere's what we learned from analyzing 1M+ process instances:",
  "created_by": "claude_code",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review",
  "destination_url": "https://twitter.com/mindaborption",
  "twitter_specific": {
    "is_thread": false,
    "thread_position": null,
    "reply_to": null,
    "quote_tweet_url": null
  },
  "media": []
}
```

### Twitter/X Thread

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440011",
  "platform": "twitter",
  "type": "post",
  "persona": "personal",
  "persona_display": "Personal",
  "content": "THREAD: 5 things I learned building process mining software for 10 years:",
  "created_by": "claude_code",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review",
  "twitter_specific": {
    "is_thread": true,
    "thread_position": 1,
    "thread_id": "thread-001"
  },
  "thread_content": [
    "1/ Data quality is everything. You can have the fanciest algorithms, but garbage in = garbage out.",
    "2/ Business users don't care about technical metrics. Translate everything to dollars and time.",
    "3/ The biggest insights often come from what's NOT in the data - the missing steps.",
    "4/ Start with one process. Perfect it. Then expand. Don't try to boil the ocean.",
    "5/ Process mining is a journey, not a destination. Continuous improvement means continuous mining."
  ]
}
```

### Twitter/X Reply

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440012",
  "platform": "twitter",
  "type": "reply",
  "persona": "mindzie",
  "persona_display": "CTO of mindzie",
  "content": "Absolutely agree! We've seen this pattern across hundreds of implementations.",
  "created_by": "engagement_agent",
  "created_at": "2024-02-21T11:00:00Z",
  "status": "pending_review",
  "context_url": "https://twitter.com/someuser/status/123456789",
  "context_title": "Tweet about process automation",
  "context_author": "@someuser"
}
```

### Reddit Post

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440020",
  "platform": "reddit",
  "type": "post",
  "persona": "personal",
  "persona_display": "Personal",
  "content": "# How we reduced invoice processing time by 70% using process mining\n\nI've been working in process mining for over a decade, and I wanted to share a recent success story...\n\n## The Problem\n...",
  "created_by": "claude_code",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review",
  "reddit_specific": {
    "subreddit": "r/processimprovement",
    "title": "How we reduced invoice processing time by 70% using process mining",
    "flair": "Case Study",
    "subreddit_url": "https://www.reddit.com/r/processimprovement/"
  }
}
```

### Reddit Comment

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440021",
  "platform": "reddit",
  "type": "comment",
  "persona": "personal",
  "persona_display": "Personal",
  "content": "Great question! The key difference between process mining and traditional BI is...",
  "created_by": "engagement_agent",
  "created_at": "2024-02-21T11:00:00Z",
  "status": "pending_review",
  "context_url": "https://www.reddit.com/r/businessintelligence/comments/abc123/",
  "context_title": "What's the difference between process mining and BI?",
  "reddit_specific": {
    "subreddit": "r/businessintelligence",
    "parent_comment": null
  }
}
```

### YouTube Comment

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440030",
  "platform": "youtube",
  "type": "comment",
  "persona": "mindzie",
  "persona_display": "CTO of mindzie",
  "content": "Excellent breakdown of the RPA vs process mining debate! One thing I'd add is that they're not mutually exclusive - process mining helps you identify WHERE to apply RPA for maximum impact.",
  "created_by": "engagement_agent",
  "created_at": "2024-02-21T11:00:00Z",
  "status": "pending_review",
  "context_url": "https://www.youtube.com/watch?v=abc123xyz",
  "context_title": "RPA vs Process Mining: Which Should You Choose?",
  "context_author": "Tech Channel"
}
```

### Email

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440040",
  "platform": "email",
  "type": "email",
  "persona": "mindzie",
  "persona_display": "CTO of mindzie",
  "content": "Hi John,\n\nIt was great meeting you at the conference last week. I wanted to follow up on our conversation about process mining...\n\nBest regards",
  "created_by": "outreach_agent",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review",
  "email_specific": {
    "to": ["john.doe@company.com"],
    "cc": [],
    "bcc": [],
    "subject": "Following up from Process Mining Summit",
    "reply_to_message_id": null,
    "attachments": []
  },
  "recipient": {
    "name": "John Doe",
    "company": "TechCorp",
    "title": "Director of Operations"
  }
}
```

### Article (Long-form Blog/LinkedIn Article)

```json
{
  "id": "550e8400-e29b-41d4-a716-446655440050",
  "platform": "blog",
  "type": "article",
  "persona": "personal",
  "persona_display": "Personal",
  "content": "# The Future of Process Mining: 5 Trends to Watch in 2024\n\n## Introduction\n\nProcess mining has evolved from a niche academic discipline...\n\n## Trend 1: AI-Powered Process Discovery\n\n...",
  "created_by": "claude_code",
  "created_at": "2024-02-21T10:30:00Z",
  "status": "pending_review",
  "article_specific": {
    "title": "The Future of Process Mining: 5 Trends to Watch in 2024",
    "subtitle": "From AI-powered discovery to real-time optimization",
    "target_platforms": ["linkedin_article", "medium", "company_blog"],
    "word_count": 1500,
    "reading_time_minutes": 7,
    "cover_image": "content/media/trends_2024_cover.png",
    "seo_keywords": ["process mining", "2024 trends", "AI", "automation"]
  },
  "destination_url": "https://www.linkedin.com/in/username/",
  "campaign_id": "thought-leadership-2024-q1"
}
```

## File Naming Convention

Files should be named with this pattern:
```
{platform}_{type}_{id}.json
```

Examples:
- `linkedin_post_550e8400.json`
- `twitter_reply_550e8401.json`
- `email_email_550e8402.json`
- `reddit_comment_550e8403.json`

## Status Transitions

```
pending_review -> approved (user approves)
pending_review -> rejected (user rejects)
approved -> posted (posting agent confirms success)
rejected -> pending_review (user reconsiders, can edit and resubmit)
```

## Rejection Metadata

When content is rejected, additional fields are added:

```json
{
  "rejection_reason": "Tone too promotional",
  "rejected_at": "2024-02-21T12:00:00Z",
  "rejected_by": "user"
}
```

## Posting Metadata

When content is posted, additional fields are added:

```json
{
  "posted_at": "2024-02-21T14:00:00Z",
  "posted_by": "posting_agent",
  "posted_url": "https://www.linkedin.com/posts/actual-post-url",
  "post_id": "platform-specific-post-id"
}
```
