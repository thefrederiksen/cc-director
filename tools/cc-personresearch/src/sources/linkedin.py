"""LinkedIn source - uses cc-linkedin CLI tool."""

import json
import logging
import re
import subprocess
import sys

from src.sources.base import BaseSource
from src.models import SourceResult

logger = logging.getLogger(__name__)


def _extract_json(text: str):
    """Extract JSON array or object from text that may have non-JSON header lines.

    cc-linkedin --format json outputs a human-readable header line before the
    JSON payload, e.g.:
        Search Results for 'Jane Doe' (people)

        [{"name": "Jane Doe", ...}]

    This function finds and parses just the JSON portion.
    """
    text = text.strip()
    if not text:
        return None

    # Try parsing the whole string first
    try:
        return json.loads(text)
    except json.JSONDecodeError:
        pass

    # Find the first [ or { which starts the JSON
    for i, ch in enumerate(text):
        if ch in ("[", "{"):
            try:
                return json.loads(text[i:])
            except json.JSONDecodeError:
                continue

    return None


class LinkedInSource(BaseSource):
    name = "linkedin"
    requires_browser = False  # Uses cc-linkedin CLI, not direct browser

    def __init__(self, *args, linkedin_workspace: str = "linkedin", **kwargs):
        super().__init__(*args, **kwargs)
        self.linkedin_workspace = linkedin_workspace

    def fetch(self) -> SourceResult:
        results = {"search_results": [], "profile": None, "urls": []}

        # Step 1: Search with company context first (more specific)
        company = self._context_company()
        search_results = []

        if company:
            query_with_company = f"{self.person_name} {company}"
            if self.verbose:
                logger.info("Searching: %s", query_with_company)
            search_results = self._search(query_with_company)

        # Fallback: search by name only
        if not search_results:
            if self.verbose:
                logger.info("Searching: %s", self.person_name)
            search_results = self._search(self.person_name)

        if not search_results:
            return SourceResult(source=self.name, status="not_found",
                                data=results)

        results["search_results"] = search_results

        # Step 2: Get detailed profile for top result
        top_result = search_results[0]
        username = top_result.get("username", "")
        if username:
            if self.verbose:
                logger.info("Fetching profile: %s", username)
            profile = self._get_profile(username)
            if profile:
                results["profile"] = profile
                profile_url = f"https://www.linkedin.com/in/{username}"
                results["urls"].append(profile_url)

        # Collect all profile URLs from search
        for sr in search_results:
            uname = sr.get("username", "")
            if uname:
                url = f"https://www.linkedin.com/in/{uname}"
                if url not in results["urls"]:
                    results["urls"].append(url)

        return SourceResult(
            source=self.name,
            status="found",
            data=results,
        )

    def _search(self, query: str) -> list[dict]:
        """Run cc-linkedin search command.

        --format and --workspace are GLOBAL options that go BEFORE the subcommand.
        """
        cmd = [
            "cc-linkedin",
            "--format", "json",
            "--workspace", self.linkedin_workspace,
            "search", query,
            "--type", "people",
            "--limit", "5",
        ]
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=90,
            )
            if result.returncode != 0:
                stderr = result.stderr.strip() if result.stderr else ""
                if self.verbose and stderr:
                    logger.warning("search stderr: %s", stderr)
                return []

            output = result.stdout.strip()
            if not output:
                if self.verbose:
                    logger.debug("search returned empty stdout")
                return []

            data = _extract_json(output)
            if data is None:
                if self.verbose:
                    logger.debug("no JSON found in output: %s", output[:200])
                return []
            if isinstance(data, list):
                return data
            if isinstance(data, dict) and "results" in data:
                return data["results"]
            return []
        except FileNotFoundError:
            if self.verbose:
                logger.warning("cc-linkedin CLI not found on PATH")
            return []
        except subprocess.TimeoutExpired:
            if self.verbose:
                logger.warning("search timed out after 90s")
            return []

    def _get_profile(self, username: str) -> dict:
        """Run cc-linkedin profile command.

        --format and --workspace are GLOBAL options that go BEFORE the subcommand.
        """
        cmd = [
            "cc-linkedin",
            "--format", "json",
            "--workspace", self.linkedin_workspace,
            "profile", username,
        ]
        try:
            result = subprocess.run(
                cmd,
                capture_output=True,
                text=True,
                timeout=90,
            )
            if result.returncode != 0:
                stderr = result.stderr.strip() if result.stderr else ""
                if self.verbose and stderr:
                    logger.warning("profile stderr: %s", stderr)
                return {}

            output = result.stdout.strip()
            if not output:
                return {}

            data = _extract_json(output)
            return data if isinstance(data, dict) else {}
        except FileNotFoundError:
            return {}
        except subprocess.TimeoutExpired:
            if self.verbose:
                logger.warning("profile timed out after 90s")
            return {}
