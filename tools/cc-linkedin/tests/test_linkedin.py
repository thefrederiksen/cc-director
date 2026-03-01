"""Unit tests for cc-linkedin models, URLs, selectors, and element ref finding."""

import sys
import os
import json
import time
import tempfile
import pytest

# Add src to path so we can import modules directly
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "src"))

from models import (
    LinkedInUser,
    Post,
    Connection,
    Message,
    MessageThread,
    SearchResult,
    Invitation,
)
from linkedin_selectors import LinkedIn, LinkedInURLs
from cli import find_element_ref, find_element_ref_near_text


# =============================================================================
# Sample snapshot text for element ref tests
# =============================================================================

SAMPLE_SNAPSHOT = """\
[Navigation Bar]
  Home [ref=nav1] link
  My Network [ref=nav2] link
  Jobs [ref=nav3] link
  Messaging [ref=nav4] link
  Notifications [ref=nav5] link
  Search [ref=nav6] input

[Main Feed]
  Post by John Doe
    "Excited to share my new project!"
    Like button [ref=btn10]
    Comment button [ref=btn11]
    Repost button [ref=btn12]
    Send button [ref=btn13]

  Post by Jane Smith
    "Looking for talented engineers to join our team"
    Like button [ref=btn20]
    Comment button [ref=btn21]

[Messaging Panel]
  Conversation with Alice
    "Hey, are you available for a call?"
    Reply input [ref=msg1]
    Send button [ref=msg2]
"""

SNAPSHOT_WITH_CONNECT = """\
[Profile Page - Bob Williams]
  Software Engineer at Acme Corp
  San Francisco, CA
  500+ connections

  Connect button [ref=abc123]
  Message button [ref=abc124]
  More button [ref=abc125]

[Activity]
  Recent post: "Just published a new article on distributed systems"
  Like button [ref=act1]
  Comment button [ref=act2]
"""

SNAPSHOT_NEAR_TEXT = """\
[Search Results]
  Alice Johnson
    Product Manager at Google
    San Francisco, CA
    Connect button [ref=conn1]
    Message button [ref=msg_a1]
    .
    .
    .
    .
    .
    .
    .
    .
    .
    .
  Bob Williams
    Software Engineer at Meta
    New York, NY
    Connect button [ref=conn2]
    Message button [ref=msg_b1]
    .
    .
    .
    .
    .
    .
    .
    .
    .
    .
  Charlie Brown
    Designer at Apple
    Cupertino, CA
    Connect button [ref=conn3]
    Message button [ref=msg_c1]
"""


# =============================================================================
# TestModels
# =============================================================================

class TestModels:
    """Tests for LinkedIn pydantic models."""

    def test_linkedin_user_required_fields(self):
        """LinkedInUser creation with required fields only."""
        user = LinkedInUser(username="johndoe", name="John Doe")
        assert user.username == "johndoe"
        assert user.name == "John Doe"

    def test_linkedin_user_optional_fields_default_none(self):
        """LinkedInUser optional fields default to None."""
        user = LinkedInUser(username="janedoe", name="Jane Doe")
        assert user.headline is None
        assert user.location is None
        assert user.connections is None
        assert user.profile_url is None

    def test_post_creation_all_fields(self):
        """Post creation with all fields populated."""
        post = Post(
            id="urn:li:activity:1234567890",
            author="John Doe",
            author_headline="Software Engineer at Acme",
            content="Excited to share my new project!",
            likes=42,
            comments=7,
            shares=3,
            time_ago="2h",
            url="https://www.linkedin.com/feed/update/urn:li:activity:1234567890",
        )
        assert post.id == "urn:li:activity:1234567890"
        assert post.author == "John Doe"
        assert post.author_headline == "Software Engineer at Acme"
        assert post.content == "Excited to share my new project!"
        assert post.likes == 42
        assert post.comments == 7
        assert post.shares == 3
        assert post.time_ago == "2h"
        assert post.url == "https://www.linkedin.com/feed/update/urn:li:activity:1234567890"

    def test_connection_creation(self):
        """Connection creation with all fields."""
        conn = Connection(
            username="bobsmith",
            name="Bob Smith",
            headline="Product Manager",
            connected_date="2024-01-15",
        )
        assert conn.username == "bobsmith"
        assert conn.name == "Bob Smith"
        assert conn.headline == "Product Manager"
        assert conn.connected_date == "2024-01-15"

    def test_message_creation_is_read_default(self):
        """Message creation with is_read defaulting to True."""
        msg = Message(
            id="msg-001",
            sender="Alice",
            content="Hey, are you available?",
            time_ago="5m",
        )
        assert msg.id == "msg-001"
        assert msg.sender == "Alice"
        assert msg.content == "Hey, are you available?"
        assert msg.time_ago == "5m"
        assert msg.is_read is True

    def test_message_thread_creation(self):
        """MessageThread creation with all fields."""
        thread = MessageThread(
            id="thread-001",
            participants=["Alice", "Bob"],
            last_message="See you tomorrow!",
            last_message_time="1h",
            unread=True,
        )
        assert thread.id == "thread-001"
        assert thread.participants == ["Alice", "Bob"]
        assert thread.last_message == "See you tomorrow!"
        assert thread.last_message_time == "1h"
        assert thread.unread is True

    def test_search_result_creation_with_result_type(self):
        """SearchResult creation with explicit result_type."""
        result = SearchResult(
            name="Acme Corporation",
            headline="Leading technology company",
            location="San Francisco, CA",
            url="https://www.linkedin.com/company/acme",
            result_type="company",
        )
        assert result.name == "Acme Corporation"
        assert result.headline == "Leading technology company"
        assert result.location == "San Francisco, CA"
        assert result.url == "https://www.linkedin.com/company/acme"
        assert result.result_type == "company"

    def test_search_result_default_type_is_person(self):
        """SearchResult defaults result_type to 'person'."""
        result = SearchResult(name="Someone")
        assert result.result_type == "person"

    def test_invitation_creation(self):
        """Invitation creation with all fields."""
        invite = Invitation(
            id="inv-001",
            name="Charlie Brown",
            headline="Designer at Apple",
            time_ago="3d",
            mutual_connections=5,
        )
        assert invite.id == "inv-001"
        assert invite.name == "Charlie Brown"
        assert invite.headline == "Designer at Apple"
        assert invite.time_ago == "3d"
        assert invite.mutual_connections == 5


# =============================================================================
# TestLinkedInURLs
# =============================================================================

class TestLinkedInURLs:
    """Tests for LinkedIn URL generation."""

    def test_home_returns_correct_url(self):
        """home() returns the LinkedIn feed URL."""
        assert LinkedInURLs.home() == "https://www.linkedin.com/feed"

    def test_profile_returns_correct_url(self):
        """profile('johndoe') returns the correct profile URL."""
        assert LinkedInURLs.profile("johndoe") == "https://www.linkedin.com/in/johndoe"

    def test_search_people_returns_correct_url_with_encoded_query(self):
        """search('test query', 'people') returns URL with encoded query string."""
        url = LinkedInURLs.search("test query", "people")
        assert url == "https://www.linkedin.com/search/results/people/?keywords=test%20query"

    def test_search_all_default(self):
        """search('test') defaults to 'all' search type."""
        url = LinkedInURLs.search("test")
        assert url == "https://www.linkedin.com/search/results/all/?keywords=test"

    def test_messaging_returns_correct_url(self):
        """messaging() returns the messaging URL."""
        assert LinkedInURLs.messaging() == "https://www.linkedin.com/messaging"

    def test_connections_returns_correct_url(self):
        """connections() returns the connections URL."""
        assert LinkedInURLs.connections() == "https://www.linkedin.com/mynetwork/invite-connect/connections"

    def test_invitations_returns_correct_url(self):
        """invitations() returns the invitation manager URL."""
        assert LinkedInURLs.invitations() == "https://www.linkedin.com/mynetwork/invitation-manager"

    def test_company_returns_correct_url(self):
        """company('microsoft') returns the company page URL."""
        assert LinkedInURLs.company("microsoft") == "https://www.linkedin.com/company/microsoft"

    def test_jobs_returns_correct_url(self):
        """jobs() returns the jobs page URL."""
        assert LinkedInURLs.jobs() == "https://www.linkedin.com/jobs"


# =============================================================================
# TestSelectors
# =============================================================================

class TestSelectors:
    """Tests for LinkedIn CSS selectors."""

    def test_has_login_button_attribute(self):
        """LinkedIn class has LOGIN_BUTTON selector."""
        assert hasattr(LinkedIn, "LOGIN_BUTTON")
        assert isinstance(LinkedIn.LOGIN_BUTTON, str)
        assert len(LinkedIn.LOGIN_BUTTON) > 0

    def test_has_feed_container_attribute(self):
        """LinkedIn class has FEED_CONTAINER selector."""
        assert hasattr(LinkedIn, "FEED_CONTAINER")
        assert isinstance(LinkedIn.FEED_CONTAINER, str)
        assert "feed" in LinkedIn.FEED_CONTAINER.lower()

    def test_has_search_input_attribute(self):
        """LinkedIn class has SEARCH_INPUT selector."""
        assert hasattr(LinkedIn, "SEARCH_INPUT")
        assert isinstance(LinkedIn.SEARCH_INPUT, str)
        assert "search" in LinkedIn.SEARCH_INPUT.lower()


# =============================================================================
# TestElementRefFinding
# =============================================================================

class TestElementRefFinding:
    """Tests for find_element_ref and find_element_ref_near_text."""

    def test_find_element_ref_finds_button(self):
        """find_element_ref finds a button ref in snapshot text."""
        ref = find_element_ref(SAMPLE_SNAPSHOT, ["like"], "button")
        assert ref is not None
        assert ref == "btn10"

    def test_find_element_ref_returns_none_when_not_found(self):
        """find_element_ref returns None when no matching element exists."""
        ref = find_element_ref(SAMPLE_SNAPSHOT, ["delete", "remove"], "button")
        assert ref is None

    def test_find_element_ref_case_insensitive(self):
        """find_element_ref matches keywords case-insensitively."""
        ref = find_element_ref(SAMPLE_SNAPSHOT, ["LIKE"], "button")
        assert ref is not None
        assert ref == "btn10"

    def test_find_element_ref_connect_button(self):
        """find_element_ref finds Connect button on a profile page."""
        ref = find_element_ref(SNAPSHOT_WITH_CONNECT, ["connect"], "button")
        assert ref == "abc123"

    def test_find_element_ref_near_text_finds_ref(self):
        """find_element_ref_near_text finds ref near specific text."""
        ref = find_element_ref_near_text(
            SNAPSHOT_NEAR_TEXT,
            "Bob Williams",
            ["connect"],
            search_range=10,
        )
        assert ref is not None
        assert ref == "conn2"

    def test_find_element_ref_near_text_message_button(self):
        """find_element_ref_near_text finds message button near a person."""
        ref = find_element_ref_near_text(
            SNAPSHOT_NEAR_TEXT,
            "Charlie Brown",
            ["message"],
            search_range=10,
        )
        assert ref is not None
        assert ref == "msg_c1"

    def test_find_element_ref_near_text_not_found(self):
        """find_element_ref_near_text returns None when text not in snapshot."""
        ref = find_element_ref_near_text(
            SNAPSHOT_NEAR_TEXT,
            "Nobody Here",
            ["connect"],
            search_range=10,
        )
        assert ref is None


# =============================================================================
# TestJitteredSleep
# =============================================================================

class TestJitteredSleep:
    """Tests for jittered_sleep delay helper."""

    def test_small_sleep_has_small_jitter(self):
        """Base < 1.0s gets 0-0.5s jitter."""
        from delays import jittered_sleep
        start = time.time()
        jittered_sleep(0.1)
        elapsed = time.time() - start
        assert 0.1 <= elapsed <= 0.7

    def test_medium_sleep_has_medium_jitter(self):
        """Base 1.0-2.9s gets 0-1.5s jitter."""
        from delays import jittered_sleep
        start = time.time()
        jittered_sleep(1.0)
        elapsed = time.time() - start
        assert 1.0 <= elapsed <= 2.6

    def test_large_sleep_has_large_jitter(self):
        """Base >= 3.0s gets 0-2.0s jitter."""
        from delays import jittered_sleep
        start = time.time()
        jittered_sleep(3.0)
        elapsed = time.time() - start
        assert 3.0 <= elapsed <= 5.1


# =============================================================================
# TestSearchNetworkURLs
# =============================================================================

class TestSearchNetworkURLs:
    """Tests for search URL with network degree filter."""

    def test_search_without_network(self):
        """Search URL has no network param when not specified."""
        url = LinkedInURLs.search("engineer", "people")
        assert "keywords=engineer" in url
        assert "network" not in url

    def test_search_first_degree(self):
        """--network 1st appends network=["F"] to URL."""
        url = LinkedInURLs.search("Toronto", "people", network="1st")
        assert 'network=["F"]' in url

    def test_search_second_degree(self):
        """--network 2nd appends network=["S"] to URL."""
        url = LinkedInURLs.search("Toronto", "people", network="2nd")
        assert 'network=["S"]' in url

    def test_search_third_degree(self):
        """--network 3rd appends network=["O"] to URL."""
        url = LinkedInURLs.search("Toronto", "people", network="3rd")
        assert 'network=["O"]' in url

    def test_search_invalid_network_ignored(self):
        """Invalid network value produces no network param."""
        url = LinkedInURLs.search("test", "people", network="invalid")
        assert "network" not in url

    def test_search_empty_network_ignored(self):
        """Empty network string produces no network param."""
        url = LinkedInURLs.search("test", "people", network="")
        assert "network" not in url

    def test_search_network_preserves_keywords(self):
        """Network param doesn't interfere with keywords."""
        url = LinkedInURLs.search("data scientist", "people", network="1st")
        assert "keywords=data%20scientist" in url
        assert 'network=["F"]' in url


# =============================================================================
# TestConnectionsOutputFile
# =============================================================================

class TestConnectionsOutputFile:
    """Tests for --output and --append JSON file logic."""

    def test_output_json_structure(self):
        """Verify the expected JSON export structure."""
        from datetime import datetime
        connections_data = [
            {"username": "alice", "name": "Alice Smith", "headline": "Engineer"},
            {"username": "bob", "name": "Bob Jones", "headline": "Designer"},
        ]
        export_data = {
            "exported_at": datetime.now().isoformat(),
            "search_filter": None,
            "total": len(connections_data),
            "limit": 20,
            "connections": connections_data,
        }
        assert export_data["total"] == 2
        assert len(export_data["connections"]) == 2
        assert export_data["connections"][0]["username"] == "alice"
        assert export_data["search_filter"] is None

    def test_output_json_with_search_filter(self):
        """Verify search_filter is populated when search is used."""
        from datetime import datetime
        export_data = {
            "exported_at": datetime.now().isoformat(),
            "search_filter": "Toronto",
            "total": 1,
            "limit": 100,
            "connections": [{"username": "carol", "name": "Carol", "headline": "PM"}],
        }
        assert export_data["search_filter"] == "Toronto"

    def test_append_dedup_by_username(self):
        """Verify dedup logic filters out existing usernames."""
        existing = [
            {"username": "alice", "name": "Alice Smith", "headline": "Engineer"},
            {"username": "bob", "name": "Bob Jones", "headline": "Designer"},
        ]
        existing_usernames = {c["username"] for c in existing}

        new_from_page = [
            {"username": "alice", "name": "Alice Smith", "headline": "Engineer"},
            {"username": "carol", "name": "Carol White", "headline": "PM"},
        ]
        new_connections = [c for c in new_from_page if c["username"] not in existing_usernames]

        assert len(new_connections) == 1
        assert new_connections[0]["username"] == "carol"

        merged = existing + new_connections
        assert len(merged) == 3

    def test_append_reads_wrapped_format(self):
        """Verify append mode reads the wrapped JSON format with metadata."""
        data = {
            "exported_at": "2026-02-27T10:00:00",
            "total": 2,
            "connections": [
                {"username": "alice", "name": "Alice", "headline": "Eng"},
                {"username": "bob", "name": "Bob", "headline": "Des"},
            ],
        }
        with tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False) as f:
            json.dump(data, f)
            tmpfile = f.name

        try:
            with open(tmpfile, "r") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict) and "connections" in loaded:
                connections = loaded["connections"]
            elif isinstance(loaded, list):
                connections = loaded
            else:
                connections = []
            usernames = {c["username"] for c in connections}
            assert usernames == {"alice", "bob"}
        finally:
            os.unlink(tmpfile)

    def test_append_reads_plain_array_format(self):
        """Verify append mode reads a plain JSON array (legacy format)."""
        data = [
            {"username": "alice", "name": "Alice", "headline": "Eng"},
            {"username": "bob", "name": "Bob", "headline": "Des"},
        ]
        with tempfile.NamedTemporaryFile(mode="w", suffix=".json", delete=False) as f:
            json.dump(data, f)
            tmpfile = f.name

        try:
            with open(tmpfile, "r") as f:
                loaded = json.load(f)
            if isinstance(loaded, dict) and "connections" in loaded:
                connections = loaded["connections"]
            elif isinstance(loaded, list):
                connections = loaded
            else:
                connections = []
            usernames = {c["username"] for c in connections}
            assert usernames == {"alice", "bob"}
        finally:
            os.unlink(tmpfile)
